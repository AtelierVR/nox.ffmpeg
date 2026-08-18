using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using FFmpeg.AutoGen;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Helpers;
using Nox.VideoPlayer;
using UnityEngine;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Nox.FFmpeg.Handlers;
using UnityEngine.Events;
using IHandler = Nox.FFmpeg.Base.IHandler;

namespace Nox.FFmpeg {
	public unsafe class Player : MonoBehaviour, IVideoPlayer, IVideoPlayerTexture, IVideoPlayerDetails, IVideoPlayerResolution {

		[Header("Playback")]
		public string Url;
		public bool AutoPlay = true;
		public int AvSyncType = Constants.AV_SYNC_AUDIO_MASTER;

		public string Title { get; internal set; } = null;
		public string Subtitle { get; internal set; } = null;

		// ── Output subscriptions ──────────────────────────────────────────

		/// Subscribe to receive raw float PCM for custom audio processing.
		public event Action<float[], int, int> OnAudioSamples;

		// ── Current frame ─────────────────────────────────────────────

		public Texture2D Texture 
			=> State?.GetHandler<VideoHandler>()?.Frame;

		public UnityEvent<IVideoPlayer, Texture2D> OnTexture { get; } = new();

		public Vector2Int Resolution 
			=> State?.GetHandler<VideoHandler>()?.Resolution ?? Vector2Int.zero;

		public UnityEvent<IVideoPlayer, Vector2Int> OnResolution { get; } = new();

		
		// ── Audio output ─────────────────────────────────────────────────
		/// Fires on the main thread when a new AudioClip is created (stream opened).
		public UnityEvent<AudioClip> OnClip = new();

		/// Streaming AudioClip backed by the decoded PCM stream (owned by AudioHandler).
		public AudioClip Clip
			=> State?.GetHandler<AudioHandler>()?.Clip;

		/// Sample rate used for the streaming AudioClip.
		public int AudioSampleRate 
			=> State?.GetHandler<AudioHandler>()?.SampleRate ?? 0;

		/// Update the audio hardware buffer latency fed to the video clock (call from main thread).
		public void SetAudioLatency(double seconds) { 
			if (State != null) 
				State.AudioHwBufSize = seconds; 
		}
		
		/// Master clock in seconds (NaN when not playing).
		public double MasterClock 
			=> State?.GetMasterClock() 
				?? double.NaN;

		// ── Public state ──────────────────────────────────────────────────
		public bool IsPlaying
			=> State is { Paused: false };
		
		public bool IsPaused
			=> State is { Paused: true };
		
		public bool IsBuffering
			=> State != null && !State.Handlers.All(h => h.HasEnoughPackets);

		// ── IVideoPlayer ──────────────────────────────────────────────────
		public UnityEvent<IVideoPlayer, Exception> OnError { get; } = new();
		public UnityEvent<IVideoPlayer, string>    OnMessage { get; } = new();
		public UnityEvent<IVideoPlayer, float>     OnVolume { get; } = new();
		public UnityEvent<IVideoPlayer, double>    OnSeek { get; } = new();
		public UnityEvent<IVideoPlayer, bool>      OnLoop { get; } = new();
		public UnityEvent<IVideoPlayer>            OnPlay { get; } = new();
		public UnityEvent<IVideoPlayer>            OnPause { get; } = new();
		public UnityEvent<IVideoPlayer>            OnResume { get; } = new();
		public UnityEvent<IVideoPlayer>            OnStop { get; } = new();

		private float _volume = 1f;
		private bool  _loop;

		public float Volume {
			get => State == null ? _volume : State.AudioVolume / 128f;
			set {
				_volume = Mathf.Clamp01(value);
				if (State != null)
					State.AudioVolume = (int)(_volume * 128);
				OnVolume.Invoke(this, _volume);
			}
		}

		public double Time {
			get {
				var t = MasterClock;
				if (double.IsNaN(t))
					return t;
				var d = Duration;
				if (double.IsNaN(d) || d <= 0)
					return t;
				// The audio clock keeps extrapolating after EOF; clamp so the
				// counter stops at the end instead of running past the duration.
				return Math.Clamp(t, 0, d);
			}
			set => Seek(value);
		}

		public double Duration {
			get {
				if (State == null || State.Ic == null)
					return double.NaN;
				return State.Ic->duration / (double)ffmpeg.AV_TIME_BASE;
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
				if (State != null)
					State.Loop = value;
				OnLoop.Invoke(this, value);
			}
		}

		/// <summary>
		/// Play from a query (URL, file path, or search term). If the query is not
		/// a direct media URL, it is resolved through the VideoPlayer resolve pipeline.
		/// </summary>
		public void Play(string query) {
			if (string.IsNullOrWhiteSpace(query))
				return;
			if (VideoPlayerResolver.IsMedia(this, query)) {
				Open(query);
				return;
			}
			PlayerResolver.ResolveAndOpenAsync(this, new VideoFetchOptions { Query = query }).Forget();
		}

		public void Stop() {
			Close();
			OnStop.Invoke(this);
		}

		internal void FireError(string message) {
			OnMessage.Invoke(this, message);
			OnError.Invoke(this, new Exception(message));
		}

		// ── Private ───────────────────────────────────────────────────────

		public PlayerState State;

		public UnityEvent<PlayerState> OnStateChanged { get; } = new();

		private double _refreshTime;

		// ── Unity lifecycle ───────────────────────────────────────────────
		private void Awake() 
			=> Initializer.Initialize();

		private void Start() {
			VideoPlayerRegister.Register(this);
			if (AutoPlay && !string.IsNullOrWhiteSpace(Url))
				Play(Url);
		}

		private void Update() {
			if (State == null)
				return;

			// When not looping, pause once playback reaches the end so the
			// clock/counter stop instead of running past the duration.
			if (!Loop && State.Eof && !State.Paused && HasReachedEnd())
				Pause();

			// Drive video_refresh every frame; let it decide internally when to display
			State.VideoRefresh(Constants.REFRESH_RATE);
		}

		private bool HasReachedEnd()
			=> State.Handlers.All(h => h.HasEnded);

		// ── Public API ────────────────────────────────────────────────────
		/// Open and start playback from a URL (file, HLS, RTMP, RTSP …).
		/// When <paramref name="audioUrl"/> is provided, video and audio are opened
		/// from two separate inputs (e.g. YouTube DASH streams).
		public void Open(string videoUrl, string audioUrl = null) {
			Close();
			Debug.Log(string.IsNullOrEmpty(audioUrl)
				? $"[FFplay] Opening {videoUrl}"
				: $"[FFplay] Opening video {videoUrl} + audio {audioUrl}");

			State = new PlayerState();
			State.AvSyncType = AvSyncType;
			State.Loop       = Loop;

			// Estimate audio hw buffer latency (≈ Unity's AudioSource buffer)
			State.AudioHwBufSize = 1.0 / Constants.AUDIO_MAX_CALLBACKS_PER_SEC * 2;

			// Build the streams (flux) and their typed handlers.
			var streams  = new List<IStream>();
			if (!string.IsNullOrEmpty(audioUrl)) {
				streams.Add(new MediaStream(StreamType.Audio, audioUrl));
				streams.Add(new MediaStream(StreamType.Video, videoUrl));
			} else {
				streams.Add(new MediaStream(StreamType.Video | StreamType.Audio, videoUrl));
			}

			// Handlers own their typed logic; PlayerState just stores them.
			var audioHandler = new AudioHandler(this);
			var handlers     = new List<IHandler> {
				new VideoHandler(this),
				audioHandler,
				new SubtitleHandler()
			};

			State.Streams  = streams.ToArray();
			State.Handlers = handlers.ToArray();

			foreach (var handler in handlers) 
				handler.Start();

			State.TargetAudioFreq = audioHandler.SampleRate; // ensure SWR resamples to Unity's output rate
			State.StartReadThread();

			// Bootstrap AudioHwBufSize from DSP config; refined dynamically by AudioSourceComponent
			AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
			State.AudioHwBufSize = (double)(dspLen * dspCount) / audioHandler.SampleRate;

			// AudioHandler owns the clip; Player only surfaces it through OnClip.
			OnClip.Invoke(audioHandler.CreateClip());
			OnPlay.Invoke(this);

			OnStateChanged.Invoke(State);
		}

		[ContextMenu("Play")]
		public void Play()
			=> Play(Url);

		[ContextMenu("Stop")]
		public void Close() {
			// Handlers own their resources (AudioHandler owns and destroys its clip).
			if (State != null)
				foreach (var handler in State.Handlers)
					handler.Stop();

			if (State == null) return;
			var vs = State;
			State = null;
			// Dispose on a background thread — ReadTid.Join() can block seconds on network streams
			Task.Run(() => vs.Dispose());
		}

		[ContextMenu("Pause")]
		public void Pause() {
			if (State == null || State.Paused)
				return;
			State.TogglePause();
			OnPause.Invoke(this);
		}

		[ContextMenu("Resume")]
		public void Resume() {
			if (State is not { Paused: true })
				return;
			// Restart from the beginning when resuming after the end.
			if (!Loop && State.Eof && HasReachedEnd())
				State.StreamSeek(0, 0, false);
			State.TogglePause();
			OnResume.Invoke(this);
		}

		public void Seek(double seconds) {
			State?.StreamSeek((long)(seconds * ffmpeg.AV_TIME_BASE), 0, false);
			OnSeek.Invoke(this, seconds);
		}

		public void SeekRelative(double delta) {
			if (State == null)
				return;
			double pos = State.GetMasterClock();
			if (double.IsNaN(pos))
				pos = (double)State.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			State.StreamSeek((long)(pos * ffmpeg.AV_TIME_BASE), (long)(delta * ffmpeg.AV_TIME_BASE), false);
		}

		private void OnDestroy() {
			VideoPlayerRegister.UnRegister(this);
			Close();
		}
		
		private void OnDisable()
			=> State?.TogglePause();
		
		private void OnEnable() {
			if (State is { Paused: true })
				State.TogglePause();
		}
	}
}