using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Utils {
	public unsafe class Player : MonoBehaviour {

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
			if (AutoPlay && !string.IsNullOrWhiteSpace(Url))
				Open(Url);
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
		/// Open and start playback from a URL (file, HLS, RTMP, RTSP …)
		public void Open(string url) {
			Close();
			Url = url;
			Debug.Log($"[FFplay] Opening {url}");

			_vs            = new VideoState(url);
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
		}

		[ContextMenu("Play")]
		public void Play()
			=> Open(Url);

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
		}

		[ContextMenu("Resume")]
		public void Resume() {
			if (_vs is not { Paused: true })
				return;
			_vs.TogglePause();
		}

		public void Seek(double seconds)
			=> _vs?.StreamSeek((long)(seconds * ffmpeg.AV_TIME_BASE), 0, false);

		public void SeekRelative(double delta) {
			if (_vs == null)
				return;
			double pos = _vs.GetMasterClock();
			if (double.IsNaN(pos))
				pos = (double)_vs.SeekPos / ffmpeg.AV_TIME_BASE;
			pos += delta;
			_vs.StreamSeek((long)(pos * ffmpeg.AV_TIME_BASE), (long)(delta * ffmpeg.AV_TIME_BASE), false);
		}

		private void OnDestroy()
			=> Close();
		
		private void OnDisable()
			=> _vs?.TogglePause();
		
		private void OnEnable() {
			if (_vs is { Paused: true })
				_vs.TogglePause();
		}
	}
}