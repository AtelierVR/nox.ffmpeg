using System;
using System.Collections.Generic;
using System.IO;
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
	public unsafe class Player : MonoBehaviour, IVideoPlayer {

		[Header("Playback")]
		public string Url;
		public bool AutoPlay = true;
		public int AvSyncType = Constants.AV_SYNC_AUDIO_MASTER;

		// ── Output subscriptions ──────────────────────────────────────────
		public UnityEvent<Texture2D> OnFrame = new();
		/// Subscribe to receive raw float PCM for custom audio processing.
		public event Action<float[], int, int> OnAudioSamples;

		// ── Current frame ─────────────────────────────────────────────
		public Texture2D Frame 
			=> state?.GetHandler<VideoHandler>()?.Frame;

		
		// ── Audio output ─────────────────────────────────────────────────
		/// Fires on the main thread when a new AudioClip is created (stream opened).
		public UnityEvent<AudioClip> OnClip = new();

		/// Streaming AudioClip backed by the decoded PCM stream (owned by AudioHandler).
		public AudioClip Clip
			=> state?.GetHandler<AudioHandler>()?.Clip;

		/// Sample rate used for the streaming AudioClip.
		public int AudioSampleRate 
			=> state?.GetHandler<AudioHandler>()?.SampleRate ?? 0;

		/// Update the audio hardware buffer latency fed to the video clock (call from main thread).
		public void SetAudioLatency(double seconds) { 
			if (state != null) 
				state.AudioHwBufSize = seconds; 
		}
		
		/// Master clock in seconds (NaN when not playing).
		public double MasterClock 
			=> state?.GetMasterClock() 
				?? double.NaN;

		// ── Public state ──────────────────────────────────────────────────
		public bool IsPlaying
			=> state is { Paused: false };
		
		public bool IsPaused
			=> state is { Paused: true };
		
		public bool IsBuffering
			=> state != null
				&& ((state.Video.StreamIndex >= 0 && state.VideoQ.NbPackets < Constants.MIN_FRAMES / 4)
					|| (state.Audio.StreamIndex >= 0 && state.AudioQ.NbPackets < Constants.MIN_FRAMES / 4));

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
			get => state == null ? _volume : state.AudioVolume / 128f;
			set {
				_volume = Mathf.Clamp01(value);
				if (state != null)
					state.AudioVolume = (int)(_volume * 128);
				OnVolume.Invoke(this, _volume);
			}
		}

		public double Time {
			get => MasterClock;
			set => Seek(value);
		}

		public double Duration {
			get {
				if (state == null || state.Ic == null)
					return double.NaN;
				return state.Ic->duration / (double)ffmpeg.AV_TIME_BASE;
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
		internal PlayerState state;

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
			if (state == null)
				return;

			// Drive video_refresh every frame; let it decide internally when to display
			state.VideoRefresh(Constants.REFRESH_RATE);
		}

		// ── Public API ────────────────────────────────────────────────────
		/// Open and start playback from a URL (file, HLS, RTMP, RTSP …).
		/// When <paramref name="audioUrl"/> is provided, video and audio are opened
		/// from two separate inputs (e.g. YouTube DASH streams).
		public void Open(string videoUrl, string audioUrl = null) {
			Close();
			Debug.Log(string.IsNullOrEmpty(audioUrl)
				? $"[FFplay] Opening {videoUrl}"
				: $"[FFplay] Opening video {videoUrl} + audio {audioUrl}");

			state = new PlayerState();
			state.AvSyncType = AvSyncType;

			// Estimate audio hw buffer latency (≈ Unity's AudioSource buffer)
			state.AudioHwBufSize = 1.0 / Constants.AUDIO_MAX_CALLBACKS_PER_SEC * 2;

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

			state.Streams  = streams.ToArray();
			state.Handlers = handlers.ToArray();

			foreach (var handler in handlers) 
				handler.Start();

			state.TargetAudioFreq = audioHandler.SampleRate; // ensure SWR resamples to Unity's output rate
			state.StartReadThread();

			// Bootstrap AudioHwBufSize from DSP config; refined dynamically by AudioSourceComponent
			AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
			state.AudioHwBufSize = (double)(dspLen * dspCount) / audioHandler.SampleRate;

			// AudioHandler owns the clip; Player only surfaces it through OnClip.
			OnClip.Invoke(audioHandler.CreateClip());
			OnPlay.Invoke(this);
		}

		[ContextMenu("Play")]
		public void Play()
			=> Play(Url);

		[ContextMenu("Stop")]
		public void Close() {
			// Handlers own their resources (AudioHandler owns and destroys its clip).
			if (state != null)
				foreach (var handler in state.Handlers)
					handler.Stop();

			if (state == null) return;
			var vs = state;
			state = null;
			// Dispose on a background thread — ReadTid.Join() can block seconds on network streams
			Task.Run(() => vs.Dispose());
		}

		[ContextMenu("Pause")]
		public void Pause() {
			if (state == null || state.Paused)
				return;
			state.TogglePause();
			OnPause.Invoke(this);
		}

		[ContextMenu("Resume")]
		public void Resume() {
			if (state is not { Paused: true })
				return;
			state.TogglePause();
			OnResume.Invoke(this);
		}

		public void Seek(double seconds) {
			state?.StreamSeek((long)(seconds * ffmpeg.AV_TIME_BASE), 0, false);
			OnSeek.Invoke(this, seconds);
		}

		public void SeekRelative(double delta) {
			if (state == null)
				return;
			double pos = state.GetMasterClock();
			if (double.IsNaN(pos))
				pos = (double)state.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			state.StreamSeek((long)(pos * ffmpeg.AV_TIME_BASE), (long)(delta * ffmpeg.AV_TIME_BASE), false);
		}

		private void OnDestroy() {
			VideoPlayerRegister.UnRegister(this);
			Close();
		}
		
		private void OnDisable()
			=> state?.TogglePause();
		
		private void OnEnable() {
			if (state is { Paused: true })
				state.TogglePause();
		}
	}
}