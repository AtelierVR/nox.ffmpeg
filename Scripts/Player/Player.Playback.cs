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

		public double Time {
			get {
				var t = MasterClock;
				if (double.IsNaN(t)) return t;
				var d = Duration;
				if (double.IsNaN(d) || d <= 0) return t;
				return Math.Clamp(t, 0, d);
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
			if (VideoPlayerResolver.IsMedia(this, query)) {
				Open(new Flux(StreamType.Av, query));
				return;
			}
			this.ResolveAndOpenAsync(new VideoFetchOptions { Query = query }).Forget();
		}

		public void Open(params Flux[] flux) {
			Close();
			if (flux.Length == 0) {
				Debug.LogWarning($"[FFplay] No flux.");
				return;
			}
			foreach (var f in flux)
				if (string.IsNullOrEmpty(f.Url)) {
					Debug.LogWarning($"[FFplay] Flux {f.Type} is empty.");
					return;
				}
			Debug.Log($"[FFplay] Opening {string.Join(", ", flux.Select(e => e.Url))}");

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
			InternalState?.StreamSeek((long)(seconds * ffmpeg.AV_TIME_BASE), 0, false);
			OnSeek.Invoke(this, seconds);
		}

		public void SeekRelative(double delta) {
			if (InternalState == null)
				return;
			double pos = InternalState.MasterClock;
			if (double.IsNaN(pos))
				pos = (double)InternalState.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			InternalState.StreamSeek((long)(pos * ffmpeg.AV_TIME_BASE), (long)(delta * ffmpeg.AV_TIME_BASE), false);
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