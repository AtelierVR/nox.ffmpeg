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
using UnityEngine.Events;

namespace Nox.FFmpeg.Utils {
	public unsafe class Player : MonoBehaviour, IVideoPlayer {

		[Header("Playback")]
		public string Url;
		public bool AutoPlay = true;
		public int AvSyncType = Constants.AV_SYNC_AUDIO_MASTER;

		[Header("Debug")]
		[Tooltip("Dump decoded PCM to a raw f32le file (before Unity audio) for debugging.")]
		public bool DumpAudioPcm = false;
		[Tooltip("Raw f32le interleaved dump. Convert: ffmpeg -f f32le -ar <rate> -ac <channels> -i dump.f32 dump.wav")]
		public string DumpAudioPath = "audio_dump.f32";

		// ── Output subscriptions ──────────────────────────────────────────
		public UnityEvent<Texture2D> OnFrame = new();
		/// Subscribe to receive raw float PCM for custom audio processing.
		public event Action<float[], int, int> OnAudioSamples;

		// ── Current frame ─────────────────────────────────────────────
		public Texture2D Frame => _videoHandler?.Frame;
		// ── Audio output ─────────────────────────────────────────────────
		/// Fires on the main thread when a new AudioClip is created (stream opened).
		public UnityEvent<AudioClip> OnClip = new();
		/// Streaming AudioClip backed by the decoded PCM stream.
		public AudioClip Clip { get; private set; }
		/// Sample rate used for the streaming AudioClip.
		public int AudioSampleRate => _sampleRate;
		/// Current PCM write cursor in the ring buffer (sample-frames mod clip.samples).
		public int PcmWritePos => _audioHandler?.PcmWritePos ?? 0;
		/// Update the audio hardware buffer latency fed to the video clock (call from main thread).
		public void SetAudioLatency(double seconds) { if (_vs != null) _vs.AudioHwBufSize = seconds; }
		/// Pull decoded PCM sample-frames from the audio handler (main thread).
		/// Returns the number of frames copied into <paramref name="dst"/>.
		public int ReadAudio(float[] dst, int frames)
			=> _audioHandler?.Read(dst, frames) ?? 0;

		// ── PCM debug dump (bypasses the AudioClip / AudioSource) ────────
		private long _pcmDataBytes;
		private int _pcmRate, _pcmChannels;

		private void HandleAudioSamples(float[] data, int channels, int freq) {
			if (_pcmDump != null) {
				var bytes = new byte[data.Length * 4];
				Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
				_pcmDump.Write(bytes, 0, bytes.Length);
				_pcmDataBytes += bytes.Length;
			}
			OnAudioSamples?.Invoke(data, channels, freq);
		}

		private void StartPcmDump() {
			StopPcmDump();
			if (string.IsNullOrWhiteSpace(DumpAudioPath)) return;
			try {
				_pcmDump = new FileStream(DumpAudioPath, FileMode.Create, FileAccess.Write, FileShare.Read);
				_pcmRate = _sampleRate;
				_pcmChannels = _audioChannels;
				_pcmDataBytes = 0;

				// Minimal float32 WAV header (sizes patched on Stop).
				var header = new byte[44];
				System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
				System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
				System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
				BitConverter.GetBytes(16).CopyTo(header, 16);                    // fmt chunk size
				BitConverter.GetBytes((ushort)3).CopyTo(header, 20);             // IEEE float
				BitConverter.GetBytes((ushort)_pcmChannels).CopyTo(header, 22);  // channels
				BitConverter.GetBytes(_pcmRate).CopyTo(header, 24);              // sample rate
				BitConverter.GetBytes(_pcmRate * _pcmChannels * 4).CopyTo(header, 28); // byte rate
				BitConverter.GetBytes((ushort)(_pcmChannels * 4)).CopyTo(header, 32);  // block align
				BitConverter.GetBytes((ushort)32).CopyTo(header, 34);            // bits per sample
				System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
				_pcmDump.Write(header, 0, header.Length);
				_pcmDump.Flush();
			} catch (Exception e) {
				Debug.LogError($"[FFplay] PCM dump failed: {e.Message}");
				_pcmDump = null;
			}
		}

		private void StopPcmDump() {
			if (_pcmDump == null) return;
			try {
				var riff = BitConverter.GetBytes((int)(36 + _pcmDataBytes));
				var dataSize = BitConverter.GetBytes((int)_pcmDataBytes);
				_pcmDump.Seek(4, SeekOrigin.Begin);
				_pcmDump.Write(riff, 0, 4);
				_pcmDump.Seek(40, SeekOrigin.Begin);
				_pcmDump.Write(dataSize, 0, 4);
			} finally {
				_pcmDump.Dispose();
				_pcmDump = null;
			}
		}
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

		// ── Streams & handlers ────────────────────────────────────────────
		public IStream[]  Streams  { get; private set; } = Array.Empty<IStream>();
		public IHandler[] Handlers { get; private set; } = Array.Empty<IHandler>();

		private VideoHandler _videoHandler;
		private AudioHandler _audioHandler;

		// ── Private ───────────────────────────────────────────────────────
		private VideoState _vs;
		private FileStream _pcmDump;

		private int _sampleRate;
		private int _audioChannels;
		private double _refreshTime;

		// ── Unity lifecycle ───────────────────────────────────────────────
		private void Awake() {
			Initializer.Initialize();
			_sampleRate = AudioSettings.outputSampleRate;
			// FFmpeg resamples to stereo s16 and Unity AudioSource reliably plays
			// only 1–2 channel clips. Force stereo so the AudioClip layout always
			// matches the resampled PCM layout (no channel interleave mismatch).
			_audioChannels = 2;
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

			// Build the streams (flux) and their typed handlers.
			var streams  = new List<IStream> { new MediaStream(StreamType.Video, url) };
			var handlers = new List<IHandler>();

			_videoHandler = new VideoHandler(_vs);
			_videoHandler.OnFrame.AddListener(frame => OnFrame.Invoke(frame));
			_videoHandler.Start();
			handlers.Add(_videoHandler);

			if (!string.IsNullOrEmpty(audioUrl))
				streams.Add(new MediaStream(StreamType.Audio, audioUrl));

			_audioHandler = new AudioHandler(_vs, _audioChannels, _sampleRate);
			if (DumpAudioPcm) StartPcmDump();
			_audioHandler.OnSamples += HandleAudioSamples;
			_audioHandler.Start();
			handlers.Add(_audioHandler);

			Streams  = streams.ToArray();
			Handlers = handlers.ToArray();

			_vs.StartReadThread();

			// Bootstrap AudioHwBufSize from DSP config; refined dynamically by AudioSourceComponent
			AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
			_vs.AudioHwBufSize = (double)(dspLen * dspCount) / _sampleRate;

			if (Clip) Destroy(Clip);
			// Non-stream circular clip (1 s). AudioSourceComponent fills it from the
			// main thread via SetData, mirroring the voice-output ring buffer.
			// Decoding happens on a dedicated thread in AudioHandler — never on
			// Unity's audio thread — so the stream can't glitch on decode hiccups.
			Clip = AudioClip.Create("FFplay", _sampleRate, _audioChannels, _sampleRate, false);
			OnClip.Invoke(Clip);
			OnPlay.Invoke(this);
		}

		[ContextMenu("Play")]
		public void Play()
			=> Play(Url);

		[ContextMenu("Stop")]
		public void Close() {
			StopPcmDump();
			if (Clip) { Destroy(Clip); Clip = null; }

			_videoHandler?.Stop();
			_audioHandler?.Stop();
			_videoHandler = null;
			_audioHandler = null;
			Streams  = Array.Empty<IStream>();
			Handlers = Array.Empty<IHandler>();

			if (_vs == null) return;
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