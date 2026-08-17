using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Utils {
	/// Decodes video frames and converts them to a <see cref="Texture2D"/>.
	internal unsafe sealed class VideoHandler : IHandler {
		public StreamType Type => StreamType.Video;
		public bool IsRunning { get; private set; }
		public Texture2D Frame { get; private set; }
		public UnityEvent<Texture2D> OnFrame { get; } = new();

		private readonly VideoState _vs;

		private Converter _converter;
		private int _convW, _convH;
		private AVPixelFormat _convFormat;
		private byte[] _rgb;

		public VideoHandler(VideoState vs)
			=> _vs = vs;

		public void Start() {
			IsRunning = true;
			_vs.OnVideoFrameReady = HandleFrame;
		}

		public void Stop() {
			IsRunning = false;
			_vs.OnVideoFrameReady = null;
			_converter?.Dispose();
			_converter = null;
			if (Frame) {
				UnityEngine.Object.Destroy(Frame);
				Frame = null;
			}
		}

		private void HandleFrame(IntPtr framePtr) {
			AVFrame* frame = (AVFrame*)framePtr;
			if (frame == null || frame->data[0] == null || frame->format == -1)
				return;

			int w = frame->width,
				h = frame->height;
			int len = w * h * 3;

			// Recreate the converter only when the source size/format changes.
			var fmt = (AVPixelFormat)frame->format;
			if (_converter == null || _convW != w || _convH != h || _convFormat != fmt) {
				_converter?.Dispose();
				_converter = new Converter(
					new System.Drawing.Size(w, h), fmt,
					new System.Drawing.Size(w, h), AVPixelFormat.AV_PIX_FMT_RGB24);
				_convW      = w;
				_convH      = h;
				_convFormat = fmt;
			}

			if (_rgb == null || _rgb.Length != len)
				_rgb = new byte[len];

			var converted = _converter.Convert(*frame);
			Marshal.Copy((IntPtr)converted.data[0], _rgb, 0, len);

			Upload(w, h, _rgb);
		}

		private void Upload(int w, int h, byte[] buf) {
			if (!Frame || Frame.width != w || Frame.height != h) {
				if (Frame)
					UnityEngine.Object.Destroy(Frame);
				Frame = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "FFplay" };
			}
			Frame.LoadRawTextureData(buf);
			Frame.Apply(false);
			OnFrame.Invoke(Frame);
		}
	}

	/// Feeds decoded PCM into a small ring buffer on a dedicated thread.
	/// The main thread pulls from the ring via <see cref="Read"/> and writes it
	/// into a non-stream AudioClip with SetData (see AudioSourceComponent).
	internal sealed class AudioHandler : IHandler {
		public StreamType Type => StreamType.Audio;
		public bool IsRunning { get; private set; }
		/// Total sample-frames produced by the fill thread so far.
		public int PcmWritePos => _writePos;
		public event Action<float[], int, int> OnSamples;

		private readonly VideoState _vs;
		private readonly int _channels;
		private readonly int _sampleRate;

		private readonly int _ringFrames;
		private readonly float[] _ring;
		private volatile int _writePos;
		private volatile int _readPos;
		private Thread _fillThread;
		private volatile bool _running;

		private const int ChunkFrames = 2048;

		public AudioHandler(VideoState vs, int channels, int sampleRate) {
			_vs         = vs;
			_channels   = channels;
			_sampleRate = sampleRate;
			_ringFrames = Math.Max(sampleRate / 4, ChunkFrames * 4);  // ~250 ms ring
			_ring       = new float[_ringFrames * channels];
		}

		public void Start() {
			IsRunning = true;
			_running  = true;
			_fillThread = new Thread(FillLoop) { IsBackground = true, Name = "ffplay_audio_fill" };
			_fillThread.Start();
		}

		public void Stop() {
			IsRunning = false;
			_running  = false;
			if (_fillThread != null) {
				_fillThread.Join(1000);
				_fillThread = null;
			}
		}

		private void FillLoop() {
			var chunk = new float[ChunkFrames * _channels];
			while (_running) {
				// Wait until the audio stream is actually open before decoding.
				if (_vs.AudioStream < 0) { Thread.Sleep(1); continue; }

				int free = _ringFrames - (_writePos - _readPos);
				if (free < ChunkFrames) { Thread.Sleep(1); continue; }

				_vs.AudioCallback(chunk, _channels, _sampleRate);
				WriteChunk(chunk);
				_writePos += ChunkFrames;
				OnSamples?.Invoke(chunk, _channels, _sampleRate);
			}
		}

		private void WriteChunk(float[] chunk) {
			int frames      = chunk.Length / _channels;
			int start       = (_writePos % _ringFrames) * _channels;
			int firstFrames = Math.Min(frames, _ringFrames - (_writePos % _ringFrames));
			Array.Copy(chunk, 0, _ring, start, firstFrames * _channels);
			if (firstFrames < frames)
				Array.Copy(chunk, firstFrames * _channels, _ring, 0, (frames - firstFrames) * _channels);
		}

		/// Pull up to <paramref name="frames"/> sample-frames of decoded PCM (main thread).
		/// Returns the number of frames actually copied.
		public int Read(float[] dst, int frames) {
			int available = _writePos - _readPos;
			if (available <= 0 || frames <= 0) return 0;

			int count = Math.Min(available, frames);
			int start = (_readPos % _ringFrames) * _channels;
			int firstFrames = Math.Min(count, _ringFrames - (_readPos % _ringFrames));
			Array.Copy(_ring, start, dst, 0, firstFrames * _channels);
			if (firstFrames < count)
				Array.Copy(_ring, 0, dst, firstFrames * _channels, (count - firstFrames) * _channels);
			_readPos += count;
			return count;
		}
	}

	/// Placeholder handler for subtitles.
	internal sealed class SubtitleHandler : IHandler {
		public StreamType Type => StreamType.Subtitle;
		public bool IsRunning { get; private set; }

		public void Start() => IsRunning = true;
		public void Stop()  => IsRunning = false;
	}
}
