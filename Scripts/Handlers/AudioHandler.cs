using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Nox.FFmpeg;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Handlers {
	/// Feeds decoded PCM into a small ring buffer on a dedicated thread.
	/// The main thread pulls from the ring via <see cref="Read"/> and writes it
	/// into a non-stream AudioClip with SetData (see AudioSourceComponent).
	public sealed class AudioHandler : IHandler {
		public override StreamType Type 
			=> StreamType.Audio;

		/// Cached on the main thread; AudioSettings.outputSampleRate can't be read
		/// from the decoder thread.
		public int SampleRate { get; }

		public int Channels 
			=> 2;

		/// Total sample-frames produced by the fill thread so far.
		public int PcmWritePos => _writePos;
		/// Streaming AudioClip owned by this handler (output format from <see cref="SampleRate"/>/<see cref="Channels"/>).
		public AudioClip Clip { get; private set; }
		public event Action<float[], int, int> OnSamples;

		private readonly Player player;

		private readonly int _ringFrames;
		private readonly float[] _ring;
		private volatile int _writePos;
		private volatile int _readPos;
		private Thread _fillThread;
		private volatile bool _running;

		private const int ChunkFrames = 2048;

		public AudioHandler(Player p) {
			player     = p;
			SampleRate = AudioSettings.outputSampleRate;
			_ringFrames = Math.Max(SampleRate / 4, ChunkFrames * 4);  // ~250 ms ring
			_ring       = new float[_ringFrames * Channels];
		}

		public override void Start() {
			IsRunning = true;
			_running  = true;
			_fillThread = new Thread(FillLoop) { IsBackground = true, Name = "ffplay_audio_fill" };
			_fillThread.Start();
		}

		public override void Stop() {
			IsRunning = false;
			_running  = false;
			if (_fillThread != null) {
				_fillThread.Join(1000);
				_fillThread = null;
			}
			DestroyClip();
		}

		/// Create (or recreate) the non-stream circular clip matching this handler's output format.
		public AudioClip CreateClip() {
			DestroyClip();
			Clip = AudioClip.Create("FFplay", SampleRate, Channels, SampleRate, false);
			return Clip;
		}

		private void DestroyClip() {
			if (!Clip) return;
			UnityEngine.Object.Destroy(Clip);
			Clip = null;
		}

		private void FillLoop() {
			var chunk = new float[ChunkFrames * Channels];
			while (_running) {
				// Wait until the audio stream is actually open before decoding.
			if (StreamIndex < 0) { Thread.Sleep(1); continue; }
				int free = _ringFrames - (_writePos - _readPos);
				if (free < ChunkFrames) { Thread.Sleep(1); continue; }

				player.state.AudioCallback(chunk, Channels, SampleRate);
				WriteChunk(chunk);
				_writePos += ChunkFrames;
				OnSamples?.Invoke(chunk, Channels, SampleRate);
			}
		}

		private void WriteChunk(float[] chunk) {
			int frames      = chunk.Length / Channels;
			int start       = (_writePos % _ringFrames) * Channels;
			int firstFrames = Math.Min(frames, _ringFrames - (_writePos % _ringFrames));
			Array.Copy(chunk, 0, _ring, start, firstFrames * Channels);
			if (firstFrames < frames)
				Array.Copy(chunk, firstFrames * Channels, _ring, 0, (frames - firstFrames) * Channels);
		}

		/// Pull up to <paramref name="frames"/> sample-frames of decoded PCM (main thread).
		/// Returns the number of frames actually copied.
		public int Read(float[] dst, int frames) {
			int available = _writePos - _readPos;
			if (available <= 0 || frames <= 0) return 0;

			int count = Math.Min(available, frames);
			int start = (_readPos % _ringFrames) * Channels;
			int firstFrames = Math.Min(count, _ringFrames - (_readPos % _ringFrames));
			Array.Copy(_ring, start, dst, 0, firstFrames * Channels);
			if (firstFrames < count)
				Array.Copy(_ring, 0, dst, firstFrames * Channels, (count - firstFrames) * Channels);
			_readPos += count;
			return count;
		}
	}

	/// Placeholder handler for subtitles.
	internal sealed class SubtitleHandler : IHandler {
		public override StreamType Type => StreamType.Subtitle;

		public override void Start() => IsRunning = true;
		public override void Stop()  => IsRunning = false;
	}
}
