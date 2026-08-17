using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using FFmpeg.AutoGen;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Helpers;
using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Utils {
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
		public Texture2D Frame { get; private set; }
		// ── Audio output ─────────────────────────────────────────────────
		/// Fires on the main thread when a new AudioClip is created (stream opened).
		public UnityEvent<AudioClip> OnClip = new();
		/// Streaming AudioClip backed by the decoded PCM stream.
		public AudioClip Clip { get; private set; }
		/// Sample rate used for the streaming AudioClip.
		public int AudioSampleRate => _sampleRate;
		/// Current PCM write cursor in the ring buffer (sample-frames mod clip.samples).
		public int PcmWritePos => _pcmWritePos;
		/// Update the audio hardware buffer latency fed to the video clock (call from main thread).
		public void SetAudioLatency(double seconds) { if (_vs != null) _vs.AudioHwBufSize = seconds; }
		/// Master clock in seconds (NaN when not playing).
		public double MasterClock => _vs?.GetMasterClock() ?? double.NaN;
		// ── Public state ──────────────────────────────────────────────────
		public bool IsPlaying
			=> _vs is { Paused: false };
		
		public bool IsPaused
			=> _vs is { Paused: true };
		
		public bool IsBuffering
			=> _vs != null
				&& ((_vs.VideoStream >= 0 && _vs.VideoQ.NbPackets < Constants.MIN_FRAMES / 4)
					|| (_vs.AudioStream >= 0 && _vs.AudioQ.NbPackets < Constants.MIN_FRAMES / 4));

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
			get => _vs == null ? _volume : _vs.AudioVolume / 128f;
			set {
				_volume = Mathf.Clamp01(value);
				if (_vs != null)
					_vs.AudioVolume = (int)(_volume * 128);
				OnVolume.Invoke(this, _volume);
			}
		}

		public double Time {
			get => MasterClock;
			set => Seek(value);
		}

		public double Duration {
			get {
				if (_vs == null || _vs.Ic == null)
					return double.NaN;
				return _vs.Ic->duration / (double)ffmpeg.AV_TIME_BASE;
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
		private VideoState _vs;

		private int _sampleRate;
		private int _audioChannels;
		private volatile int _pcmWritePos; // write cursor in the ring, sample-frames mod clip length
		private double _refreshTime;

		// ── Unity lifecycle ───────────────────────────────────────────────
		private void Awake() {
			Initializer.Initialize();
			_sampleRate = AudioSettings.outputSampleRate;
			_audioChannels = AudioSettings.speakerMode switch {
				AudioSpeakerMode.Mono        => 1,
				AudioSpeakerMode.Stereo      => 2,
				AudioSpeakerMode.Prologic    => 2,
				AudioSpeakerMode.Quad        => 4,
				AudioSpeakerMode.Surround    => 5,
				AudioSpeakerMode.Mode5point1 => 6,
				AudioSpeakerMode.Mode7point1 => 8,
				_                            => 2
			};
		}

		private void Start() {
			VideoPlayerRegister.Register(this);
			if (AutoPlay && !string.IsNullOrWhiteSpace(Url))
				Play(Url);
		}

		private void Update() {
			if (_vs == null)
				return;

			// Drive video_refresh every frame; let it decide internally when to display
			_vs.VideoRefresh(Constants.REFRESH_RATE);
		}

		// Called by VideoState when a decoded frame is ready.
		// OnVideoFrameReady is only ever invoked from VideoRefresh → Update (main thread).
		// No UniTask.Post needed — UploadFrame runs inline, same frame, no extra latency.
		private void HandleVideoFrame(IntPtr framePtr) {
			AVFrame* frame = (AVFrame*)framePtr;
			if (frame == null || frame->data[0] == null || frame->format == -1)
				return;

			int w = frame->width,
				h = frame->height;
			int    len = w * h * 3;
			byte[] buf = new byte[ len ];

			// Convert to RGB24 via swscale (main thread — frame is valid for the duration of this call)
			using var sws = new Converter(
				new System.Drawing.Size(w, h), (AVPixelFormat)frame->format,
				new System.Drawing.Size(w, h), AVPixelFormat.AV_PIX_FMT_RGB24);
			var converted = sws.Convert(*frame);
			Marshal.Copy((IntPtr)converted.data[0], buf, 0, len);

			UploadFrame(w, h, buf);
		}

		private void UploadFrame(int w, int h, byte[] buf) {
			if (!Frame || Frame.width != w || Frame.height != h) {
				if (Frame)
					Destroy(Frame);
				Frame = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "FFplay" };
			}
			Frame.LoadRawTextureData(buf);
			Frame.Apply(false);
			OnFrame.Invoke(Frame);
		}

		// ── Audio (PCMReaderCallback — runs on audio thread) ─────────────
		private void OnPCMRead(float[] data) {
			if (_vs == null) { Array.Clear(data, 0, data.Length); return; }
			_vs.AudioCallback(data, _audioChannels, _sampleRate);
			OnAudioSamples?.Invoke(data, _audioChannels, _sampleRate);
			// Advance write cursor so AudioSourceComponent can measure real buffer depth
			_pcmWritePos = (_pcmWritePos + data.Length / _audioChannels) % _sampleRate;
		}

		// ── Public API ────────────────────────────────────────────────────
		/// Open and start playback from a URL (file, HLS, RTMP, RTSP …).
		/// When <paramref name="audioUrl"/> is provided, video and audio are opened
		/// from two separate inputs (e.g. YouTube DASH streams).
		public void Open(string url, string audioUrl = null) {
			Close();
			Debug.Log(string.IsNullOrEmpty(audioUrl)
				? $"[FFplay] Opening {url}"
				: $"[FFplay] Opening video {url} + audio {audioUrl}");

			_vs = new VideoState(url) { AudioFilename = audioUrl };
			_vs.AvSyncType      = AvSyncType;
			_vs.TargetAudioFreq = _sampleRate; // ensure SWR resamples to Unity's output rate

			// Estimate audio hw buffer latency (≈ Unity's AudioSource buffer)
			_vs.AudioHwBufSize = 1.0 / Constants.AUDIO_MAX_CALLBACKS_PER_SEC * 2;

			_vs.OnVideoFrameReady = HandleVideoFrame;
			_vs.StartReadThread();

			// Bootstrap AudioHwBufSize from DSP config; refined dynamically by AudioSourceComponent
			AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
			_vs.AudioHwBufSize = (double)(dspLen * dspCount) / _sampleRate;

			_pcmWritePos = 0;
			if (Clip) Destroy(Clip);
			Clip = AudioClip.Create("FFplay", _sampleRate, _audioChannels, _sampleRate, true, OnPCMRead);
			OnClip.Invoke(Clip);
			OnPlay.Invoke(this);
		}

		[ContextMenu("Play")]
		public void Play()
			=> Play(Url);

		[ContextMenu("Stop")]
		public void Close() {
			if (Clip) { Destroy(Clip); Clip = null; }
			if (_vs == null) return;
			_vs.OnVideoFrameReady = null;
			var vs = _vs;
			_vs = null;
			// Dispose on a background thread — ReadTid.Join() can block seconds on network streams
			Task.Run(() => vs.Dispose());
		}

		[ContextMenu("Pause")]
		public void Pause() {
			if (_vs == null || _vs.Paused)
				return;
			_vs.TogglePause();
			OnPause.Invoke(this);
		}

		[ContextMenu("Resume")]
		public void Resume() {
			if (_vs is not { Paused: true })
				return;
			_vs.TogglePause();
			OnResume.Invoke(this);
		}

		public void Seek(double seconds) {
			_vs?.StreamSeek((long)(seconds * ffmpeg.AV_TIME_BASE), 0, false);
			OnSeek.Invoke(this, seconds);
		}

		public void SeekRelative(double delta) {
			if (_vs == null)
				return;
			double pos = _vs.GetMasterClock();
			if (double.IsNaN(pos))
				pos = (double)_vs.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			_vs.StreamSeek((long)(pos * ffmpeg.AV_TIME_BASE), (long)(delta * ffmpeg.AV_TIME_BASE), false);
		}

		private void OnDestroy() {
			VideoPlayerRegister.UnRegister(this);
			Close();
		}
		
		private void OnDisable()
			=> _vs?.TogglePause();
		
		private void OnEnable() {
			if (_vs is { Paused: true })
				_vs.TogglePause();
		}
	}
}