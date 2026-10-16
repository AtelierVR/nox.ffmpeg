using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using FFmpeg.AutoGen;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Base;
using Nox.FFmpeg.Handlers;
using Nox.FFmpeg.Utils;
using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;
using IHandler = Nox.FFmpeg.Base.IHandler;
using LogType = Nox.CCK.Utils.LogType;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.FFmpeg {
	public unsafe partial class Player {
		private bool _loop;

		// ── Events ────────────────────────────────────────────────────────
		public UnityEvent<IVideoPlayer, LogType, string> OnMessage { get; } = new();
		public UnityEvent<IVideoPlayer, double> OnSeek { get; } = new();
		public UnityEvent<IVideoPlayer, bool> OnLoop { get; } = new();
		public UnityEvent<IVideoPlayer> OnStream { get; } = new();

		// ── Timeline & Clock ──────────────────────────────────────────────
		public double MasterClock => InternalState?.MasterClock ?? double.NaN;

		/// <summary>
		/// Position (seconds) the user asked for on the last seek, or NaN when there is none.
		/// Two purposes: reported immediately as <see cref="Time"/> while the clocks are
		/// invalid, and used as a floor until the clock catches up (the audio clock lags the
		/// decoded position by the output latency) so the timeline never steps back.
		/// </summary>
		private double _seekTarget = double.NaN;
		/// <summary>Unscaled time of that request (bounds how long the floor applies).</summary>
		private float _seekTargetAt;
		/// <summary>How long the requested position may keep overriding the clock.</summary>
		private const float SeekFloorGrace = 1.5f;
		/// <summary>A clock within this many seconds of the target is considered caught up.</summary>
		private const double SeekFloorTolerance = 0.15;
		/// <summary>Position (AV_TIME_BASE ticks) of that same request.</summary>
		private long _seekRequestTicks = long.MinValue;
		/// <summary>Last position reported by a valid master clock.</summary>
		private double _lastKnownTime = double.NaN;
		/// <summary>True while the seek has not been serviced by the read thread yet.</summary>
		private bool _seeking;
		/// <summary>Unscaled time the current seek was requested at (safety timeout).</summary>
		private float _seekStartedAt;
		/// <summary>A seek that never completes (dead stream…) must not freeze the timeline.</summary>
		private const float SeekTimeout = 5f;

		/// <summary>
		/// True while the seek has been requested and the demuxer has not been repositioned
		/// yet.
		/// </summary>
		public bool IsSeeking
			=> _seeking;

		public double Time {
			get {
				var t = MasterClock;
				if (!double.IsNaN(t)) {
					var    d = Duration;
					double v = double.IsNaN(d) || d <= 0 ? t : Math.Clamp(t, 0, d);
					// Never step back below the position the user asked for: right after a seek
					// the audio clock is still behind by the output latency.
					return !double.IsNaN(_seekTarget) && v + SeekFloorTolerance < _seekTarget
						? _seekTarget
						: v;
				}
				// Clocks invalid (seek in flight, initial buffering): report the requested
				// position, or the last known one, instead of 00:00.
				return !double.IsNaN(_seekTarget) ? _seekTarget : _lastKnownTime;
			}
			set => Seek(value);
		}

		public double Duration {
			get {
				if (InternalState == null || InternalState.Context == null)
					return double.NaN;
				return InternalState.Context->duration / (double)ffmpeg.AV_TIME_BASE;
			}
		}

		public double Progress {
			get {
				var d = Duration;
				if (double.IsNaN(d) || d <= 0) return 0;
				var t = Time;
				if (double.IsNaN(t)) return 0;
				return Math.Clamp(t / d, 0, 1);
			}
		}

		public bool Loop {
			get => _loop;
			set {
				_loop = value;
				if (InternalState != null)
					InternalState.Loop = value;
				OnLoop.Invoke(this, value);
			}
		}

		// ── Playback Controls ─────────────────────────────────────────────
		public void Play(string query) {
			if (string.IsNullOrWhiteSpace(query))
				return;
			_url = query; // remember the last launched url
			if (VideoPlayerResolver.IsMedia(this, query)) {
				Open(new Flux(StreamType.Av, query));
				return;
			}
			this.ResolveAndOpenAsync(new VideoFetchOptions { Query = query }).Forget();
		}

		public void Open(params Flux[] flux) {
			Close();
			if (flux.Length == 0) {
				Logger.LogWarning($"No flux.", this, nameof(Player));
				return;
			}
			foreach (var f in flux)
				if (string.IsNullOrEmpty(f.Url)) {
					Logger.LogWarning($"Flux {f.Type} is empty.", this, nameof(Player));
					return;
				}
			Logger.Log($"Opening {string.Join(", ", flux.Select(e => e.Url))}", this, nameof(Player));

			InternalState = new PlayerState {
				AvSyncType = AvSyncType,
				Loop       = Loop,
				Player     = this,
			};
			InternalState.OnEndReached.AddListener(OnEndReached);
			InternalState.OnMessage.AddListener(Log);
			InternalState.OnStreamChanged.AddListener(OnStreamChanged);

			var streams = new List<IStream>();
			foreach (var f in flux) 
				streams.Add(new MediaStream(f.Type, f.Url, f.Headers));

			var audio = new AudioHandler(InternalState);

			var video = new VideoHandler(InternalState);
			video.OnTexture.AddListener(frame => OnTexture.Invoke(this, frame));
			video.OnResolution.AddListener(res => OnResolution.Invoke(this, res));

			var subtitle = new SubtitleHandler(InternalState);

			InternalState.Streams = streams.ToArray();
			InternalState.Handlers = new IHandler[] {
				video,
				audio,
				subtitle
			};

			foreach (var handler in InternalState.Handlers) 
				handler.Start();

			InternalState.StartReadThread();

			AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
			InternalState.Audio.Latency = (double)(dspLen * dspCount) / audio.SampleRate;

			audio.CreateClip(); // the AudioSources are driven from this clip (AudioSourceComponent)
			OnClip.Invoke(this, Clip);
			OnStateChanged.Invoke(InternalState);
			OnState.Invoke(this, State.Playing);
		}

		[ContextMenu("Play")]
		public void Play() => Play(Url);

		[ContextMenu("Stop")]
		public void Close() {
			_seeking          = false;
			_seekTarget       = double.NaN; // a new stream must not inherit the previous position
			_seekRequestTicks = long.MinValue;
			_lastKnownTime    = double.NaN;
			if (InternalState != null)
				foreach (var handler in InternalState.Handlers)
					handler.Stop();
			if (InternalState == null) return;
			var vs = InternalState;
			InternalState = null;
			OnStream.Invoke(this); // no stream anymore: let the listeners reset what they show
			Task.Run(() => vs.Dispose());
		}

		public void Stop() {
			Close();
			OnState.Invoke(this, State.Stopped);
		}

		[ContextMenu("Pause")]
		public void Pause() {
			if (InternalState == null || InternalState.Paused)
				return;
			InternalState.TogglePause();
			OnState.Invoke(this, State.Paused);
		}

		[ContextMenu("Resume")]
		public void Resume() {
			if (InternalState is not { Paused: true })
				return;
			if (!Loop && InternalState.Eof && InternalState.HasReachedEnd)
				InternalState.StreamSeek(0, 0, false);
			InternalState.TogglePause();
			OnState.Invoke(this, State.Playing);
		}

		public void Seek(double seconds) {
			var state = InternalState;
			if (state == null)
				return;

			var duration = Duration;
			if (!double.IsNaN(duration) && duration > 0)
				seconds = Math.Clamp(seconds, 0, duration);
			if (seconds < 0)
				seconds = 0;

			bool alreadySeeking = _seeking;
			_seekRequestTicks = (long)(seconds * ffmpeg.AV_TIME_BASE);
			_seekTarget       = seconds;
			_seekTargetAt     = UnityEngine.Time.unscaledTime;
			_seeking          = true;
			_seekStartedAt    = _seekTargetAt;
			state.StreamSeek(_seekRequestTicks, 0, false);
			OnSeek.Invoke(this, seconds);
			if (!alreadySeeking)
				OnState.Invoke(this, State.Seeking);
		}

		public void SeekRelative(double delta) {
			var state = InternalState;
			if (state == null)
				return;
			double pos = Time;
			if (double.IsNaN(pos))
				pos = (double)state.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			if (pos < 0)
				pos = 0;

			bool alreadySeeking = _seeking;
			_seekRequestTicks = (long)(pos * ffmpeg.AV_TIME_BASE);
			_seekTarget       = pos;
			_seekTargetAt     = UnityEngine.Time.unscaledTime;
			_seeking          = true;
			_seekStartedAt    = _seekTargetAt;
			state.StreamSeek(_seekRequestTicks, (long)(delta * ffmpeg.AV_TIME_BASE), false);
			OnSeek.Invoke(this, pos);
			if (!alreadySeeking)
				OnState.Invoke(this, State.Seeking);
		}

		/// <summary>
		/// Ends <see cref="State.Seeking"/> as soon as the demuxer reposition has been
		/// serviced (<see cref="PlayerState.SeekServicedPos"/> matches the requested
		/// position) — a demuxer seek is the only part that is really "seeking"; waiting for
		/// the clocks would add the whole output latency. The player then reports Buffering
		/// while the queues refill. Falls back to a timeout so a stalled stream cannot leave
		/// the player stuck in Seeking.
		/// </summary>
		private void CheckSeekCompleted() {
			if (!_seeking)
				return;

			var state = InternalState;
			if (state == null) {
				EndSeek();
				return;
			}

			bool serviced = state.SeekServicedPos == _seekRequestTicks;
			bool timedOut = UnityEngine.Time.unscaledTime - _seekStartedAt >= SeekTimeout;
			if (serviced || timedOut)
				EndSeek();
		}

		private void EndSeek() {
			_seeking = false;
			OnState.Invoke(this, State); // re-evaluate: Seeking → Buffering / Playing
		}

		/// <summary>
		/// Maintains the timeline fallbacks: remembers the last valid clock and releases the
		/// seek floor once the clock caught up (or after a grace period, in case the stream
		/// landed far from the target, e.g. on a keyframe).
		/// </summary>
		private void UpdateTimelineFallback() {
			var t = MasterClock;
			if (!double.IsNaN(t))
				_lastKnownTime = t;

			if (_seeking || double.IsNaN(_seekTarget))
				return; // while seeking the floor must stay whatever the clock says

			bool caughtUp  = !double.IsNaN(t) && t + SeekFloorTolerance >= _seekTarget;
			bool graceOver = UnityEngine.Time.unscaledTime - _seekTargetAt > SeekFloorGrace;
			if (caughtUp || graceOver)
				_seekTarget = double.NaN;
		}

		private void OnEndReached() {
			if (!Loop) {
				Pause();
				OnState.Invoke(this, State.Ended);
			}
		}

		/// <summary>
		/// A stream has been opened or replaced (Play, track selection…): notify the
		/// listeners so they can refresh what depends on the stream.
		/// </summary>
		private void OnStreamChanged()
			=> OnStream.Invoke(this);
	}
}