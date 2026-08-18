using System;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using UnityEngine;
using UnityEngine.Events;
using Helper = Nox.FFmpeg.Helpers.Helper;

namespace Nox.FFmpeg.Handlers {
	/// Feeds decoded PCM into a small ring buffer on a dedicated thread.
	/// The main thread pulls from the ring via <see cref="Read"/> and writes it
	/// into a non-stream AudioClip with SetData (see AudioSourceComponent).
	public unsafe sealed class AudioHandler : IHandler {

		public AudioHandler(PlayerState state) : base(state) {
			AudioDiffAvgCoef = Math.Exp(Math.Log(0.01) / Constants.AUDIO_DIFF_AVG_NB);
			SampleRate = AudioSettings.outputSampleRate;
			_ringFrames = Math.Max(SampleRate / 4, ChunkFrames * 4);  // ~250 ms ring
			_ring       = new float[_ringFrames * Channels];
			Frames       = new FrameQueue(Packets, Constants.SAMPLE_QUEUE_SIZE, true);
			Clock      = new Clock(() => Packets.GetSerial());
		}

		public override StreamType Type 
			=> StreamType.Audio;

		public override AVMediaType[] MediaTypes
			=> new[] { AVMediaType.AVMEDIA_TYPE_AUDIO };
		
		// ── audio sync ────────────────────────────────────────────────────
		public double AudioClock;
		public int AudioClockSerial = -1;
		public double AudioDiffCum;
		public double AudioDiffAvgCoef;
		public double AudioDiffThreshold;
		public int AudioDiffAvgCount;
		public double Latency; // seconds (Unity: AudioSource latency estimate)

		// ── A/V sync with resampling ──────────────────────────────────────
		// (Unity uses OnAudioFilterRead; we track wanted_nb_samples here)
		public int AudioHwBufSizeSamples; // SDL audio hw buf in samples equivalent
		public int AudioSrcFreq,
			AudioTgtFreq;
		/// Desired output sample rate (set by Controller before StartReadThread).
		/// If 0, defaults to the stream's native rate.
		public int TargetAudioFreq;
		public int AudioSrcChannels,
			AudioTgtChannels;
		public AVSampleFormat AudioSrcFmt = AVSampleFormat.AV_SAMPLE_FMT_NONE;
		public AVSampleFormat AudioTgtFmt = AVSampleFormat.AV_SAMPLE_FMT_NONE;
		public SwrContext* SwrCtx;
		public uint AudioBuf1Size;
		public uint AudioBufSize;
		public uint AudioBufIndex;
		public uint AudioWriteBufSize;
		public int AudioVolume = 128; // SDL_MIX_MAXVOLUME
		public bool Muted;

		public byte* AudioBuf1;
		public byte* AudioBuf;

		public override void Open(int index, AVFormatContext* ic, AVCodecContext* avctx) {
			Latency = 1.0 / Constants.AUDIO_MAX_CALLBACKS_PER_SEC * 2; // default until set by controller

			AudioSrcFreq     = avctx->sample_rate;
			AudioSrcChannels = avctx->ch_layout.nb_channels;
			AudioSrcFmt      = avctx->sample_fmt;

			// Target: stereo s16 at Unity's output rate. Always stereo so the
			// interleaved PCM layout matches the AudioClip (created with 2 channels).
			AudioTgtFreq       = TargetAudioFreq > 0 
				? TargetAudioFreq 
				: avctx->sample_rate;

			AudioTgtChannels   = 2;
			AudioTgtFmt        = AVSampleFormat.AV_SAMPLE_FMT_S16;
			AudioDiffThreshold = Latency; // set by controller after opening
			StreamPtr  = ic->streams[index];
			StreamIndex = index;
			Decoder     = new Decoder(avctx, Packets, () => State.ContinueReadThread.Release());
			if ((ic->iformat->flags & ffmpeg.AVFMT_NOTIMESTAMPS) != 0) {
				Decoder.StartPts   = StreamPtr->start_time;
				Decoder.StartPtsTb = StreamPtr->time_base;
			}
			Packets.Start();
			Decoder.DecoderTid = new Thread(AudioThread) { 
				IsBackground = true, 
				Name = "ffplay_audio"
			};
			Decoder.DecoderTid.Start();

			// ensure SWR resamples to Unity's output rate
			TargetAudioFreq = SampleRate;
		}

		public override void Close() {
			Helper.AbortDecoder(Decoder, Frames);
			Decoder.Dispose();
			Decoder = null;
			fixed (SwrContext** p = &SwrCtx)
				ffmpeg.swr_free(p);
			if (AudioBuf1 != null) {
				ffmpeg.av_free(AudioBuf1);
				AudioBuf1 = null;
			}
			AudioBuf = null;
			StreamIndex = -1;
			StreamPtr   = null;
		}

		// ─────────────────────────────────────────────────────────────────
		// audio_thread
		// ─────────────────────────────────────────────────────────────────
		private void AudioThread() {
			AVFrame* frame = ffmpeg.av_frame_alloc();
			if (frame == null)
				return;

			try {
				int gotFrame;
				do {
					gotFrame = Decoder.DecodeFrame(frame, null);
					if (gotFrame < 0)
						break;
					if (gotFrame == 0)
						continue; // EOF flush

					Frame af = Frames.PeekWritable();
					if (af == null)
						break;

					AVRational tb = new AVRational { num = 1, den = frame->sample_rate };
					af.Pts      = frame->pts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
					af.Pos      = -1;
					af.Serial   = Decoder.PktSerial;
					af.Duration = ffmpeg.av_q2d(new AVRational { num = frame->nb_samples, den = frame->sample_rate });
					ffmpeg.av_frame_move_ref(af.AVFrame, frame);
					Frames.Push();

					if (Packets.Serial != Decoder.PktSerial)
						break;
				} while (gotFrame >= 0 || gotFrame == ffmpeg.AVERROR(ffmpeg.EAGAIN) || gotFrame == ffmpeg.AVERROR_EOF);
			} finally {
				ffmpeg.av_frame_free(&frame);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// audio_callback — equivalent of sdl_audio_callback
		// Called by Controller (OnAudioFilterRead / AudioClip PCM fill)
		// Fills `data` with `length` bytes of interleaved s16 → float PCM
		// ─────────────────────────────────────────────────────────────────
		public void AudioCallback(float[] output, int channels, int freq) {
			int len = output.Length; // samples total (channels interleaved)
			int pos = 0;

			while (pos < len) {
				if (AudioBufIndex >= AudioBufSize) {
					int size = AudioDecodeFrame();
					if (size < 0) {
						AudioBuf = null;
						int bps = AudioTgtChannels > 0 && AudioTgtFmt != AVSampleFormat.AV_SAMPLE_FMT_NONE
							? AudioTgtChannels * ffmpeg.av_get_bytes_per_sample(AudioTgtFmt) : 0;
						AudioBufSize = bps > 0
							? (uint)(Constants.AUDIO_MIN_BUFFER_SIZE / bps * bps)
							: (uint)Constants.AUDIO_MIN_BUFFER_SIZE;
					} else
						AudioBufSize = (uint)size;
					AudioBufIndex = 0;
				}

				int len1 = (int)(AudioBufSize - AudioBufIndex);
				int rem  = (len - pos) * sizeof(short);
				if (len1 > rem)
					len1 = rem;
				if (len1 <= 0) {
					pos = len;
					break;
				} // no data yet, fill silence

				if (!Muted && AudioBuf != null) {
					// Convert s16 interleaved → float [-1,1]
					short* src = (short*)(AudioBuf + AudioBufIndex);
					int    n   = len1 / sizeof(short);
					for (int i = 0; i < n && pos < len; i++, pos++)
						output[pos] = src[i] * (AudioVolume / (float)(128 * 32768));
				} else {
					int n = len1 / sizeof(short);
					for (int i = 0; i < n && pos < len; i++, pos++)
						output[pos] = 0f;
				}
				AudioBufIndex += (uint)len1;
			}
			AudioWriteBufSize = AudioBufSize - AudioBufIndex;

			// Update audio clock (set_clock_at equivalent)
			if (!double.IsNaN(AudioClock)) {
				double callbackTime = (double)ffmpeg.av_gettime_relative() / 1_000_000.0;
				Clock.SetAt(AudioClock - (2 * Latency + (double)AudioWriteBufSize /
						(AudioTgtChannels * freq * ffmpeg.av_get_bytes_per_sample(AudioTgtFmt))),
					AudioClockSerial, callbackTime);
				State.ExternalClock.SyncToSlave(Clock);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// synchronize_audio
		// ─────────────────────────────────────────────────────────────────
		private int SynchronizeAudio(int nbSamples) {
			int wanted = nbSamples;
			if (State.MasterSyncType == Constants.AV_SYNC_AUDIO_MASTER)
				return wanted;

			double diff = Clock.Get() - State.MasterClock;
			double avgDiff;
			if (!double.IsNaN(diff) && Math.Abs(diff) < Constants.AV_NOSYNC_THRESHOLD) {
				AudioDiffCum = diff + AudioDiffAvgCoef * AudioDiffCum;
				if (AudioDiffAvgCount < Constants.AUDIO_DIFF_AVG_NB)
					AudioDiffAvgCount++;
				else {
					avgDiff = AudioDiffCum * (1.0 - AudioDiffAvgCoef);
					if (Math.Abs(avgDiff) >= AudioDiffThreshold) {
						wanted = nbSamples + (int)(diff * AudioSrcFreq);
						int min = nbSamples * (100 - Constants.SAMPLE_CORRECTION_MAX) / 100;
						int max = nbSamples * (100 + Constants.SAMPLE_CORRECTION_MAX) / 100;
						wanted = Math.Clamp(wanted, min, max);
					}
				}
			} else {
				AudioDiffAvgCount = 0;
				AudioDiffCum      = 0;
			}
			return wanted;
		}

		// ─────────────────────────────────────────────────────────────────
		// audio_decode_frame — fills audio_buf / audio_buf_size
		// Returns: byte count in audio_buf, or -1
		// ─────────────────────────────────────────────────────────────────
		public int AudioDecodeFrame() {
			if (State.Paused)
				return -1;

			Frame af;
			do {
				af = Frames.PeekReadable();
				if (af == null)
					return -1;
				Frames.Next();
			} while (af.Serial != Packets.Serial);

			AVFrame* frame = af.AVFrame;
			int dataSize = ffmpeg.av_samples_get_buffer_size(
				null, frame->ch_layout.nb_channels, frame->nb_samples, (AVSampleFormat)frame->format, 1);

			int wantedNbSamples = SynchronizeAudio(frame->nb_samples);

			// Resampling / format conversion via SwrContext (identical to ffplay.c).
			// Recreate when the context is missing or the SOURCE format/rate/channels
			// change. Comparing against the target (S16) would recreate on every frame
			// (decoder format always differs from the target), which drops the resampler
			// tail each frame and causes periodic phase jumps for rate conversions.
			bool needResample = SwrCtx == null
				|| (AVSampleFormat)frame->format != AudioSrcFmt
				|| frame->sample_rate != AudioSrcFreq
				|| frame->ch_layout.nb_channels != AudioSrcChannels
				|| (wantedNbSamples != frame->nb_samples && SwrCtx == null);

			if (needResample) {
				fixed (SwrContext** pp = &SwrCtx) {
					ffmpeg.swr_free(pp); // properly nulls SwrCtx before realloc
					AVChannelLayout tgt = default;
					ffmpeg.av_channel_layout_default(&tgt, AudioTgtChannels);
					AVChannelLayout src = frame->ch_layout;
					int r2 = ffmpeg.swr_alloc_set_opts2(pp,
						&tgt, AudioTgtFmt, AudioTgtFreq,
						&src, (AVSampleFormat)frame->format, frame->sample_rate,
						0, null);
					ffmpeg.av_channel_layout_uninit(&tgt);
					if (r2 < 0 || *pp == null || ffmpeg.swr_init(*pp) < 0) {
						Debug.LogError("[FFplay] swr alloc/init failed");
						ffmpeg.swr_free(pp);
						return -1;
					}
				}
				AudioSrcFmt      = (AVSampleFormat)frame->format;
				AudioSrcFreq     = frame->sample_rate;
				AudioSrcChannels = frame->ch_layout.nb_channels;
			}

			int outCount = (int)((long)wantedNbSamples * AudioTgtFreq / frame->sample_rate + 256);
			int outSize  = ffmpeg.av_samples_get_buffer_size(null, AudioTgtChannels, outCount, AudioTgtFmt, 0);
			fixed (byte** pBuf1 = &AudioBuf1)
			fixed (uint* pBuf1Sz = &AudioBuf1Size)
				ffmpeg.av_fast_malloc(pBuf1, pBuf1Sz, (ulong)outSize);
			if (AudioBuf1 == null)
				return ffmpeg.AVERROR(ffmpeg.ENOMEM);

			if (wantedNbSamples != frame->nb_samples) {
				if (ffmpeg.swr_set_compensation(SwrCtx,
					(wantedNbSamples - frame->nb_samples) * AudioTgtFreq / frame->sample_rate,
					wantedNbSamples * AudioTgtFreq / frame->sample_rate) < 0) {
					Debug.LogError("[FFplay] swr_set_compensation failed");
					return -1;
				}
			}

			byte** inp  = frame->extended_data;
			byte*  outp = AudioBuf1;
			int    len2 = ffmpeg.swr_convert(SwrCtx, &outp, outCount, inp, frame->nb_samples);
			if (len2 < 0) {
				Debug.LogError("[FFplay] swr_convert failed");
				return -1;
			}
			if (len2 == outCount) {
				Debug.LogWarning("[FFplay] audio buffer probably too small");
				if (ffmpeg.swr_init(SwrCtx) < 0)
					fixed (SwrContext** p2 = &SwrCtx)
						ffmpeg.swr_free(p2);
			}
			AudioBuf = AudioBuf1;
			int resampledSize = len2 * AudioTgtChannels * ffmpeg.av_get_bytes_per_sample(AudioTgtFmt);

			if (!double.IsNaN(af.Pts))
				AudioClock = af.Pts + (double)frame->nb_samples / frame->sample_rate;
			else
				AudioClock = double.NaN;
			AudioClockSerial = af.Serial;

			return resampledSize;
		}

		/// Streaming AudioClip owned by this handler (output format from <see cref="SampleRate"/>/<see cref="Channels"/>).
		public AudioClip Clip { get; private set; }

		public UnityEvent<AudioClip> OnClip { get; } = new();

		/// Cached on the main thread; AudioSettings.outputSampleRate can't be read
		/// from the decoder thread.
		public int SampleRate { get; }

		public int Channels 
			=> 2;

		public override bool HasEnded
			=> StreamPtr == null || (Decoder != null && Decoder.Finished == Packets.Serial && Frames.NbRemaining() == 0);

		public override bool HasEnoughPackets
			=> StreamIndex < 0 || Packets.NbPackets >= Constants.MIN_FRAMES / 4;

		/// Total sample-frames produced by the fill thread so far.
		public int PcmWritePos => _writePos;
		public event Action<float[], int, int> OnSamples;


		private readonly int _ringFrames;
		private readonly float[] _ring;
		private volatile int _writePos;
		private volatile int _readPos;
		private Thread _fillThread;
		private volatile bool _running;

		private const int ChunkFrames = 2048;

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
			OnClip.Invoke(Clip);
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
				AudioCallback(chunk, Channels, SampleRate);
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
}
