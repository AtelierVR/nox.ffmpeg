// FFplay.cs — faithful C# translation of ffplay.c (FFmpeg reference player)
// Model logic is identical to ffplay.c; Controller is adapted for Unity
// (no SDL: video → Texture2D callback, audio → AudioClip / OnAudioFilterRead callback)
//
// Architecture mirrors ffplay.c exactly:
//   PacketQueue   → PacketQueue
//   FrameQueue    → FrameQueue / Frame
//   Decoder       → Decoder
//   VideoState    → VideoState  (the Model)
//   FFplayPlayer  → MonoBehaviour Controller (Unity wrapper only)
//
// Usage:
//   var player = GetComponent<FFplayPlayer>();
//   player.OnVideoFrame += tex => myRenderer.material.mainTexture = tex;
//   player.Open("rtmp://…");

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Nox.FFmpeg.Handlers;
using UnityEngine;
using Helper = Nox.FFmpeg.Utils.Helper;

namespace Nox.FFmpeg {
	

	// ─────────────────────────────────────────────────────────────────────────
	// VideoState — the Model; mirrors VideoState in ffplay.c
	// No SDL, no rendering: raises events consumed by FFplayPlayer (Controller)
	// ─────────────────────────────────────────────────────────────────────────
	public unsafe class PlayerState : IDisposable {
		// ── streams (URLs live on the IStreams; indices/AVStream* on the handlers) ──
		public AVFormatContext* Ic;
		public bool Realtime;
		public bool Eof;
		public bool Loop;

		// ── external clock (stream clocks live on their handlers) ─────────
		public Utils.Clock ExtClk;

		// ── state ─────────────────────────────────────────────────────────
		public bool AbortRequest;
		public bool Paused,
			LastPaused;
		public bool ForceRefresh;
		public int Step;
		public int AvSyncType = Constants.AV_SYNC_AUDIO_MASTER;

		public bool SeekReq;
		public bool AudioSeekReq;
		public int SeekFlags;
		public long SeekPos,
			SeekRel;
		public int ReadPauseReturn;
		public bool QueueAttachmentsReq;
		public double MaxFrameDuration;

		// ── video refresh timing ──────────────────────────────────────────
		public double FrameTimer;
		public double FrameLastReturnedTime;
		public double FrameLastFilterDelay;
		public int FrameDropsEarly,
			FrameDropsLate;

		// ── audio sync ────────────────────────────────────────────────────
		public double AudioClock;
		public int AudioClockSerial = -1;
		public double AudioDiffCum;
		public double AudioDiffAvgCoef;
		public double AudioDiffThreshold;
		public int AudioDiffAvgCount;
		public double AudioHwBufSize; // seconds (Unity: AudioSource latency estimate)

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
		public byte* AudioBuf1;
		public uint AudioBuf1Size;
		public byte* AudioBuf;
		public uint AudioBufSize;
		public uint AudioBufIndex;
		public uint AudioWriteBufSize;
		public int AudioVolume = 128; // SDL_MIX_MAXVOLUME
		public bool Muted;

		// ── threading ─────────────────────────────────────────────────────
		public Thread ReadTid;
		private Thread _audioReadTid;
		private readonly SemaphoreSlim _continueReadThread = new(0);
		private AVFormatContext* _icAudio;

		// ── Controller callbacks (Unity output) ───────────────────────────
		/// Called from VideoState (Model) when a video frame is ready.
		/// The consumer (Controller) converts it to Texture2D on the main thread.
		public Action<IntPtr> OnVideoFrameReady; // IntPtr to AVFrame*
		/// Called when a block of float PCM (stereo, target sample rate) is ready.
		public Action<float[], int, int> OnAudioSamplesReady; // data, channels, freq

		// ── typed handlers & streams (moved from the Player controller) ──
		public IStream[]  Streams  = Array.Empty<IStream>();
		public IHandler[] Handlers = Array.Empty<IHandler>();

		public AudioHandler Audio   
			=> GetHandler<AudioHandler>();
		public VideoHandler Video    
			=> GetHandler<VideoHandler>();
		public SubtitleHandler Subtitle 
			=> GetHandler<SubtitleHandler>();

		public T GetHandler<T>() where T : IHandler
			=> Array.Find(Handlers, h => h is T) as T;

		/// Main input URL (the video / combined stream).
		public string VideoUrl 
			=> GetStreamUrl(StreamType.Video);
		/// Separate audio-only input URL, or null when audio lives in the main input.
		public string AudioUrl 
			=> GetStreamUrl(StreamType.Audio);

		private string GetStreamUrl(StreamType type) {
			for (int i = 0; i < Streams.Length; i++)
				if ((Streams[i].Type & type) != 0 && !string.IsNullOrEmpty(Streams[i].Url))
					return Streams[i].Url;
			return null;
		}

		// ─────────────────────────────────────────────────────────────────
		// init_clock / stream_open equivalent
		// ─────────────────────────────────────────────────────────────────
		public PlayerState() {
			ExtClk = new Utils.Clock(() => ExtClk?.Serial ?? -1); // self-referential like ffplay.c

			AudioDiffAvgCoef = Math.Exp(Math.Log(0.01) / Constants.AUDIO_DIFF_AVG_NB);
		}

		// ─────────────────────────────────────────────────────────────────
		// get_master_sync_type
		// ─────────────────────────────────────────────────────────────────
		public int GetMasterSyncType() {
			if (AvSyncType == Constants.AV_SYNC_VIDEO_MASTER)
				return Video.StreamIndex >= 0 ? Constants.AV_SYNC_VIDEO_MASTER : Constants.AV_SYNC_AUDIO_MASTER;
			if (AvSyncType == Constants.AV_SYNC_AUDIO_MASTER)
				return Audio.StreamIndex >= 0 ? Constants.AV_SYNC_AUDIO_MASTER : Constants.AV_SYNC_EXTERNAL_CLOCK;
			return Constants.AV_SYNC_EXTERNAL_CLOCK;
		}

		// get_master_clock
		public double GetMasterClock()
			=> GetMasterSyncType() switch {
				Constants.AV_SYNC_VIDEO_MASTER => Video.VidClk.Get(),
				Constants.AV_SYNC_AUDIO_MASTER => Audio.AudClk.Get(),
				_                              => ExtClk.Get(),
			};

		// check_external_clock_speed
		public void CheckExternalClockSpeed() {
			if ((Video.StreamIndex >= 0 && Video.VideoQ.NbPackets <= Constants.EXTERNAL_CLOCK_MIN_FRAMES) ||
				(Audio.StreamIndex >= 0 && Audio.AudioQ.NbPackets <= Constants.EXTERNAL_CLOCK_MIN_FRAMES))
				ExtClk.SetSpeed(Math.Max(Constants.EXTERNAL_CLOCK_SPEED_MIN,
					ExtClk.Speed - Constants.EXTERNAL_CLOCK_SPEED_STEP));
			else if ((Video.StreamIndex < 0 || Video.VideoQ.NbPackets > Constants.EXTERNAL_CLOCK_MAX_FRAMES) &&
				(Audio.StreamIndex < 0 || Audio.AudioQ.NbPackets > Constants.EXTERNAL_CLOCK_MAX_FRAMES))
				ExtClk.SetSpeed(Math.Min(Constants.EXTERNAL_CLOCK_SPEED_MAX,
					ExtClk.Speed + Constants.EXTERNAL_CLOCK_SPEED_STEP));
			else {
				double s = ExtClk.Speed;
				if (s != 1.0)
					ExtClk.SetSpeed(s + Constants.EXTERNAL_CLOCK_SPEED_STEP * (1.0 - s) / Math.Abs(1.0 - s));
			}
		}

		// stream_seek
		public void StreamSeek(long pos, long rel, bool byBytes) {
			if (SeekReq)
				return;
			SeekPos   = pos;
			SeekRel   = rel;
			SeekFlags = (SeekFlags & ~ffmpeg.AVSEEK_FLAG_BYTE) | (byBytes ? ffmpeg.AVSEEK_FLAG_BYTE : 0);
			SeekReq       = true;
			AudioSeekReq  = true;
			_continueReadThread.Release();
		}

		// stream_toggle_pause
		public void StreamTogglePause() {
			if (Paused) {
				FrameTimer += (double)ffmpeg.av_gettime_relative() / 1_000_000.0 - Video.VidClk.LastUpdated;
				if (ReadPauseReturn != ffmpeg.AVERROR(38 /* ENOSYS */))
					Video.VidClk.Paused = false;
				Video.VidClk.Set(Video.VidClk.Get(), Video.VidClk.Serial);
			}
			ExtClk.Set(ExtClk.Get(), ExtClk.Serial);
			Paused = Audio.AudClk.Paused = Video.VidClk.Paused = ExtClk.Paused = !Paused;
		}

		public void TogglePause() {
			StreamTogglePause();
			Step = 0;
		}

		// step_to_next_frame
		public void StepToNextFrame() {
			if (Paused)
				StreamTogglePause();
			Step = 1;
		}

		// update_video_pts
		private void UpdateVideoPts(double pts, int serial) {
			Video.VidClk.Set(pts, serial);
			ExtClk.SyncToSlave(Video.VidClk);
		}

		// vp_duration
		private double VpDuration(Frame vp, Frame nextvp) {
			if (vp.Serial != nextvp.Serial)
				return 0.0;
			double d = nextvp.Pts - vp.Pts;
			if (double.IsNaN(d) || d <= 0 || d > MaxFrameDuration)
				return vp.Duration;
			return d;
		}

		// compute_target_delay
		private double ComputeTargetDelay(double delay) {
			if (GetMasterSyncType() == Constants.AV_SYNC_VIDEO_MASTER)
				return delay;
			double diff = Video.VidClk.Get() - GetMasterClock();
			double syncThr = Math.Max(Constants.AV_SYNC_THRESHOLD_MIN,
				Math.Min(Constants.AV_SYNC_THRESHOLD_MAX, delay));
			if (!double.IsNaN(diff) && Math.Abs(diff) < MaxFrameDuration) {
				if (diff <= -syncThr)
					delay = Math.Max(0, delay + diff);
				else if (diff >= syncThr && delay > Constants.AV_SYNC_FRAMEDUP_THRESHOLD)
					delay += diff;
				else if (diff >= syncThr)
					delay *= 2;
			}
			return delay;
		}

		// ─────────────────────────────────────────────────────────────────
		// video_refresh — called every REFRESH_RATE from the Controller Update
		// Returns: remaining_time suggestion
		// ─────────────────────────────────────────────────────────────────
		public double VideoRefresh(double remainingTime) {
			if (!Paused && GetMasterSyncType() == Constants.AV_SYNC_EXTERNAL_CLOCK && Realtime)
				CheckExternalClockSpeed();

			if (Video.StreamIndex < 0)
				return remainingTime;

		retry:
			if (Video.PictQ.NbRemaining() == 0)
				return remainingTime;

			Frame lastvp = Video.PictQ.PeekLast();
			Frame vp     = Video.PictQ.Peek();

			if (vp.Serial != Video.VideoQ.Serial) {
				Video.PictQ.Next();
				goto retry;
			}
			if (lastvp.Serial != vp.Serial)
				FrameTimer = (double)ffmpeg.av_gettime_relative() / 1_000_000.0;

			if (Paused)
				goto display;

			double lastDuration = VpDuration(lastvp, vp);
			double delay        = ComputeTargetDelay(lastDuration);
			double time         = (double)ffmpeg.av_gettime_relative() / 1_000_000.0;

			if (time < FrameTimer + delay)
				return Math.Min(FrameTimer + delay - time, remainingTime);

			FrameTimer += delay;
			if (delay > 0 && time - FrameTimer > Constants.AV_SYNC_THRESHOLD_MAX)
				FrameTimer = time;

			if (!double.IsNaN(vp.Pts))
				UpdateVideoPts(vp.Pts, vp.Serial);

			if (Video.PictQ.NbRemaining() > 1) {
				Frame  nextvp = Video.PictQ.PeekNext();
				double dur    = VpDuration(vp, nextvp);
				if (Step == 0 && time > FrameTimer + dur) {
					FrameDropsLate++;
					Video.PictQ.Next();
					goto retry;
				}
			}

			Video.PictQ.Next();
			ForceRefresh = true;
			if (Step != 0 && !Paused)
				StreamTogglePause();

		display:
			if (ForceRefresh && Video.PictQ.NbRemaining() > 0)
				OnVideoFrameReady?.Invoke((IntPtr)Video.PictQ.PeekLast().AVFrame);

			ForceRefresh = false;
			return remainingTime;
		}

		// ─────────────────────────────────────────────────────────────────
		// synchronize_audio
		// ─────────────────────────────────────────────────────────────────
		private int SynchronizeAudio(int nbSamples) {
			int wanted = nbSamples;
			if (GetMasterSyncType() == Constants.AV_SYNC_AUDIO_MASTER)
				return wanted;

			double diff = Audio.AudClk.Get() - GetMasterClock();
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
			if (Paused)
				return -1;

			Frame af;
			do {
				af = Audio.SampQ.PeekReadable();
				if (af == null)
					return -1;
				Audio.SampQ.Next();
			} while (af.Serial != Audio.AudioQ.Serial);

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
				Audio.AudClk.SetAt(AudioClock - (2 * AudioHwBufSize + (double)AudioWriteBufSize /
						(AudioTgtChannels * freq * ffmpeg.av_get_bytes_per_sample(AudioTgtFmt))),
					AudioClockSerial, callbackTime);
				ExtClk.SyncToSlave(Audio.AudClk);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// stream_component_open
		// ─────────────────────────────────────────────────────────────────
		public int StreamComponentOpen(int streamIndex, AVFormatContext* ic) {
			if (streamIndex < 0 || streamIndex >= (int)ic->nb_streams)
				return -1;

			AVCodecContext* avctx = ffmpeg.avcodec_alloc_context3(null);
			if (avctx == null)
				return ffmpeg.AVERROR(ffmpeg.ENOMEM);

			int ret = ffmpeg.avcodec_parameters_to_context(avctx, ic->streams[streamIndex]->codecpar);
			if (ret < 0)
				goto fail;

			avctx->pkt_timebase = ic->streams[streamIndex]->time_base;
			var codec = ffmpeg.avcodec_find_decoder(avctx->codec_id);
			if (codec == null) {
				ret = ffmpeg.AVERROR(ffmpeg.EINVAL);
				goto fail;
			}

			avctx->codec_id = codec->id;
			if (ffmpeg.avcodec_open2(avctx, codec, null) < 0) {
				ret = -1;
				goto fail;
			}

			ic->streams[streamIndex]->discard = AVDiscard.AVDISCARD_DEFAULT;

			switch (avctx->codec_type) {
				case AVMediaType.AVMEDIA_TYPE_AUDIO:
					AudioSrcFreq     = avctx->sample_rate;
					AudioSrcChannels = avctx->ch_layout.nb_channels;
					AudioSrcFmt      = avctx->sample_fmt;

					// Target: stereo s16 at Unity's output rate. Always stereo so the
					// interleaved PCM layout matches the AudioClip (created with 2 channels).
					AudioTgtFreq       = TargetAudioFreq > 0 ? TargetAudioFreq : avctx->sample_rate;
					AudioTgtChannels   = 2;
					AudioTgtFmt        = AVSampleFormat.AV_SAMPLE_FMT_S16;
					AudioDiffThreshold = AudioHwBufSize; // set by controller after opening

					Audio.StreamPtr  = ic->streams[streamIndex];
					Audio.StreamIndex = streamIndex;
					Audio.AudDec     = new Decoder(avctx, Audio.AudioQ, () => _continueReadThread.Release());
					if ((ic->iformat->flags & ffmpeg.AVFMT_NOTIMESTAMPS) != 0) {
						Audio.AudDec.StartPts   = Audio.StreamPtr->start_time;
						Audio.AudDec.StartPtsTb = Audio.StreamPtr->time_base;
					}
					Audio.AudioQ.Start();
					Audio.AudDec.DecoderTid = new Thread(AudioThread) { IsBackground = true, Name = "ffplay_audio" };
					Audio.AudDec.DecoderTid.Start();
					return 0;

				case AVMediaType.AVMEDIA_TYPE_VIDEO:
					Video.StreamIndex = streamIndex;
					Video.StreamPtr   = ic->streams[streamIndex];
					Video.VidDec      = new Decoder(avctx, Video.VideoQ, () => _continueReadThread.Release());
					Video.VideoQ.Start();
					Video.VidDec.DecoderTid = new Thread(VideoThread) { IsBackground = true, Name = "ffplay_video" };
					Video.VidDec.DecoderTid.Start();
					return 0;

				case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
					Subtitle.StreamIndex = streamIndex;
					Subtitle.StreamPtr   = ic->streams[streamIndex];
					Subtitle.SubDec         = new Decoder(avctx, Subtitle.SubtitleQ, () => _continueReadThread.Release());
					Subtitle.SubtitleQ.Start();
					Subtitle.SubDec.DecoderTid = new Thread(SubtitleThread) { IsBackground = true, Name = "ffplay_subtitle" };
					Subtitle.SubDec.DecoderTid.Start();
					return 0;
			}

		fail:
			ffmpeg.avcodec_free_context(&avctx);
			return ret;
		}

		// stream_component_close
		public void StreamComponentClose(int streamIndex, AVFormatContext* ic) {
			if (streamIndex < 0 || streamIndex >= (int)ic->nb_streams)
				return;
			var par = ic->streams[streamIndex]->codecpar;

			void AbortDecoder(Decoder d, FrameQueue fq) {
				d.Queue.Abort();
				fq.Signal();
				d.DecoderTid?.Join();
				d.DecoderTid = null;
				d.Queue.Flush();
			}

			switch (par->codec_type) {
				case AVMediaType.AVMEDIA_TYPE_AUDIO:
					AbortDecoder(Audio.AudDec, Audio.SampQ);
					Audio.AudDec.Dispose();
					Audio.AudDec = null;
					fixed (SwrContext** p = &SwrCtx)
						ffmpeg.swr_free(p);
					if (AudioBuf1 != null) {
						ffmpeg.av_free(AudioBuf1);
						AudioBuf1 = null;
					}
					AudioBuf          = null;
					Audio.StreamIndex = -1;
					Audio.StreamPtr   = null;
					break;

				case AVMediaType.AVMEDIA_TYPE_VIDEO:
					AbortDecoder(Video.VidDec, Video.PictQ);
					Video.VidDec.Dispose();
					Video.VidDec      = null;
					Video.StreamIndex = -1;
					Video.StreamPtr   = null;
					break;

				case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
					AbortDecoder(Subtitle.SubDec, Subtitle.SubpQ);
					Subtitle.SubDec.Dispose();
					Subtitle.SubDec         = null;
					Subtitle.StreamIndex = -1;
					Subtitle.StreamPtr   = null;
					break;
			}
			ic->streams[streamIndex]->discard = AVDiscard.AVDISCARD_ALL;
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
					gotFrame = Audio.AudDec.DecodeFrame(frame, null);
					if (gotFrame < 0)
						break;
					if (gotFrame == 0)
						continue; // EOF flush

					Frame af = Audio.SampQ.PeekWritable();
					if (af == null)
						break;

					AVRational tb = new AVRational { num = 1, den = frame->sample_rate };
					af.Pts      = frame->pts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);
					af.Pos      = -1;
					af.Serial   = Audio.AudDec.PktSerial;
					af.Duration = ffmpeg.av_q2d(new AVRational { num = frame->nb_samples, den = frame->sample_rate });
					ffmpeg.av_frame_move_ref(af.AVFrame, frame);
					Audio.SampQ.Push();

					if (Audio.AudioQ.Serial != Audio.AudDec.PktSerial)
						break;
				} while (gotFrame >= 0 || gotFrame == ffmpeg.AVERROR(ffmpeg.EAGAIN) || gotFrame == ffmpeg.AVERROR_EOF);
			} finally {
				ffmpeg.av_frame_free(&frame);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// video_thread
		// ─────────────────────────────────────────────────────────────────
		private void VideoThread() {
			AVFrame* frame = ffmpeg.av_frame_alloc();
			if (frame == null)
				return;

			AVStream* videoSt   = Video.StreamPtr;
			AVRational tb        = videoSt->time_base;
			AVRational frameRate = ffmpeg.av_guess_frame_rate(Ic, videoSt, null);

			try {
				for (;;) {
					int ret = GetVideoFrame(frame);
					if (ret < 0)
						break;
					if (ret == 0)
						continue;

					double duration = (frameRate.num != 0 && frameRate.den != 0)
						? ffmpeg.av_q2d(new AVRational { num = frameRate.den, den = frameRate.num }) : 0;
					double pts = frame->pts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : frame->pts * ffmpeg.av_q2d(tb);

					ret = QueuePicture(frame, pts, duration, frame->pts, Video.VidDec.PktSerial);
					ffmpeg.av_frame_unref(frame);
					if (Video.VideoQ.Serial != Video.VidDec.PktSerial)
						break;
					if (ret < 0)
						break;
				}
			} finally {
				ffmpeg.av_frame_free(&frame);
			}
		}

		// get_video_frame
		private int GetVideoFrame(AVFrame* frame) {
			int gotPicture = Video.VidDec.DecodeFrame(frame, null);
			if (gotPicture < 0)
				return -1;
			if (gotPicture == 0)
				return 0; // EOF

			var videoSt = Video.StreamPtr;
			double dpts = frame->pts != ffmpeg.AV_NOPTS_VALUE
				? ffmpeg.av_q2d(videoSt->time_base) * frame->pts : double.NaN;

			frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(Ic, videoSt, frame);

			// framedrop early
			if (!double.IsNaN(dpts)) {
				double diff = dpts - GetMasterClock();
				if (!double.IsNaN(diff) && Math.Abs(diff) < Constants.AV_NOSYNC_THRESHOLD
					&& diff < 0
					&& Video.VidDec.PktSerial == Video.VidClk.Serial
					&& Video.VideoQ.NbPackets != 0) {
					FrameDropsEarly++;
					ffmpeg.av_frame_unref(frame);
					return 0;
				}
			}
			return 1;
		}

		// queue_picture
		private int QueuePicture(AVFrame* srcFrame, double pts, double duration, long pos, int serial) {
			Frame vp = Video.PictQ.PeekWritable();
			if (vp == null)
				return -1;
			vp.Sar      = srcFrame->sample_aspect_ratio;
			vp.Uploaded = false;
			vp.Width    = srcFrame->width;
			vp.Height   = srcFrame->height;
			vp.Format   = srcFrame->format;
			vp.Pts      = pts;
			vp.Duration = duration;
			vp.Pos      = pos;
			vp.Serial   = serial;
			ffmpeg.av_frame_move_ref(vp.AVFrame, srcFrame);
			Video.PictQ.Push();
			return 0;
		}

		// subtitle_thread (simplified — no rendering in this port)
		private void SubtitleThread() {
			for (;;) {
				Frame sp = Subtitle.SubpQ.PeekWritable();
				if (sp == null)
					return;

				AVSubtitle sub         = default;
				int        gotSubtitle = Subtitle.SubDec.DecodeFrame(null, &sub);
				if (gotSubtitle < 0)
					break;

				if (gotSubtitle != 0 && sub.format == 0) {
					sp.Pts    = sub.pts != ffmpeg.AV_NOPTS_VALUE ? sub.pts / (double)ffmpeg.AV_TIME_BASE : 0;
					sp.Serial = Subtitle.SubDec.PktSerial;
					sp.Width  = (int)Subtitle.SubDec.Avctx->width;
					sp.Height = (int)Subtitle.SubDec.Avctx->height;
					Subtitle.SubpQ.Push();
				} else if (gotSubtitle != 0)
					ffmpeg.avsubtitle_free(&sub);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// stream_has_enough_packets
		// ─────────────────────────────────────────────────────────────────
		private static bool StreamHasEnoughPackets(AVStream* st, int streamId, PacketQueue q) {
			return streamId < 0
				|| q.AbortRequest
				|| st == null
				|| (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0
				|| (q.NbPackets > Constants.MIN_FRAMES
					&& (q.Duration == 0 || ffmpeg.av_q2d(st->time_base) * q.Duration > 1.0));
		}

		// is_realtime
		private static bool IsRealtime(AVFormatContext* s) {
			string name = Marshal.PtrToStringAnsi((IntPtr)s->iformat->name) ?? "";
			if (name == "rtp" || name == "rtsp" || name == "sdp")
				return true;
			string url = Marshal.PtrToStringAnsi((IntPtr)s->url) ?? "";
			if (s->pb != null && (url.StartsWith("rtp:") || url.StartsWith("udp:")))
				return true;
			return false;
		}

		// decode_interrupt_cb
		private int DecodeInterruptCb()
			=> AbortRequest ? 1 : 0;

		// ─────────────────────────────────────────────────────────────────
		// read_thread
		// ─────────────────────────────────────────────────────────────────
		public void StartReadThread() {
			ReadTid = new Thread(ReadThread) { IsBackground = true, Name = "ffplay_read" };
			ReadTid.Start();
		}

		private void ReadThread() {
			AVFormatContext* ic  = null;
			AVPacket*        pkt = ffmpeg.av_packet_alloc();
			if (pkt == null) {
				SignalQuit();
				return;
			}

			try {
				ic = ffmpeg.avformat_alloc_context();
				if (ic == null) {
					SignalQuit();
					return;
				}

				// interrupt callback
				var self    = GCHandle.Alloc(this);
				var selfPtr = (void*)GCHandle.ToIntPtr(self);
				var cbDelegate = new AVIOInterruptCB_callback(opaque => {
					var h = GCHandle.FromIntPtr((IntPtr)opaque);
					return h.IsAllocated && ((PlayerState)h.Target).AbortRequest ? 1 : 0;
				});
				var cb = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(cbDelegate) };
				ic->interrupt_callback = new AVIOInterruptCB { callback = cb, opaque = selfPtr };

				string videoUrl = VideoUrl ?? (Streams.Length > 0 ? Streams[0].Url : null);
				bool hasSeparateAudio = !string.IsNullOrEmpty(AudioUrl) && !string.Equals(AudioUrl, videoUrl);

				int err = ffmpeg.avformat_open_input(&ic, videoUrl, null, null);
				if (err < 0) {
					Debug.LogError($"[FFplay] Cannot open {videoUrl}: {Helper.AvErr(err)}");
					self.Free();
					SignalQuit();
					return;
				}

				Ic = ic;

				if (ic->pb != null)
					ic->pb->eof_reached = 0; // ffplay.c hack

				err = ffmpeg.avformat_find_stream_info(ic, null);
				if (err < 0)
					Debug.LogWarning($"[FFplay] {videoUrl}: could not find codec parameters");

				MaxFrameDuration = (ic->iformat->flags & ffmpeg.AVFMT_TS_DISCONT) != 0 ? 10.0 : 3600.0;
				Realtime         = IsRealtime(ic);

				// select best streams (audio may come from a separate input via AudioUrl)
				int[] stIndex = new int[ (int)AVMediaType.AVMEDIA_TYPE_NB ];
				for (int i = 0; i < stIndex.Length; i++)
					stIndex[i] = -1;

				stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] =
					ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
				stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] = hasSeparateAudio
					? -1
					: ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO,
						-1, stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], null, 0);
				stIndex[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] =
					ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_SUBTITLE,
						-1,
						stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0
							? stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO]
							: stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], null, 0);

				if (stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO] >= 0)
					StreamComponentOpen(stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO], ic);

				if (stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
					StreamComponentOpen(stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], ic);

				if (stIndex[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
					StreamComponentOpen(stIndex[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE], ic);

				if (hasSeparateAudio)
					StartAudioReadThread();

				if (Video.StreamIndex < 0 && Audio.StreamIndex < 0) {
					Debug.LogError($"[FFplay] Failed to open streams in {videoUrl}");
					SignalQuit();
					return;
				}

				// main demux loop
				for (;;) {
					if (AbortRequest)
						break;

					// pause/resume
					if (Paused != LastPaused) {
						LastPaused = Paused;
						if (Paused)
							ReadPauseReturn = ffmpeg.av_read_pause(ic);
						else
							ffmpeg.av_read_play(ic);
					}

					// seek
					if (SeekReq) {
						long seekMin = SeekRel > 0 ? SeekPos - SeekRel + 2 : long.MinValue;
						long seekMax = SeekRel < 0 ? SeekPos - SeekRel - 2 : long.MaxValue;
						int  r2      = ffmpeg.avformat_seek_file(ic, -1, seekMin, SeekPos, seekMax, SeekFlags);
						if (r2 < 0)
							Debug.LogError($"[FFplay] seek error: {Helper.AvErr(r2)}");
						else {
							if (Audio.StreamIndex >= 0)
								Audio.AudioQ.Flush();
							if (Subtitle.StreamIndex >= 0)
								Subtitle.SubtitleQ.Flush();
							if (Video.StreamIndex >= 0)
								Video.VideoQ.Flush();
							ExtClk.Set((SeekFlags & ffmpeg.AVSEEK_FLAG_BYTE) != 0
								? double.NaN : SeekPos / (double)ffmpeg.AV_TIME_BASE, 0);
						}
						SeekReq             = false;
						QueueAttachmentsReq = true;
						Eof                 = false;
						if (Paused)
							StepToNextFrame();
					}

					if (QueueAttachmentsReq) {
						var videoSt = Video.StreamPtr;
						if (videoSt != null && (videoSt->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0) {
							if (ffmpeg.av_packet_ref(pkt, &videoSt->attached_pic) >= 0) {
								Video.VideoQ.Put(pkt);
								Video.VideoQ.PutNullPacket(pkt, Video.StreamIndex);
							}
						}
						QueueAttachmentsReq = false;
					}

					// buffer full — wait
					bool enoughPackets =
						StreamHasEnoughPackets(Audio.StreamPtr, Audio.StreamIndex, Audio.AudioQ) &&
						StreamHasEnoughPackets(Video.StreamPtr, Video.StreamIndex, Video.VideoQ) &&
						StreamHasEnoughPackets(Subtitle.StreamPtr, Subtitle.StreamIndex, Subtitle.SubtitleQ);
					if (Audio.AudioQ.Size + Video.VideoQ.Size + Subtitle.SubtitleQ.Size > Constants.MAX_QUEUE_SIZE || enoughPackets) {
						_continueReadThread.Wait(10);
						continue;
					}

					// auto-loop when finished (only when looping is enabled)
					if (Loop
						&& !Paused
						&& (Audio.StreamPtr == null || (Audio.AudDec != null && Audio.AudDec.Finished == Audio.AudioQ.Serial && Audio.SampQ.NbRemaining() == 0))
						&& (Video.StreamPtr == null || (Video.VidDec != null && Video.VidDec.Finished == Video.VideoQ.Serial && Video.PictQ.NbRemaining() == 0))) {
						StreamSeek(0, 0, false); // loop
						continue;
					}

					int ret2 = ffmpeg.av_read_frame(ic, pkt);
					if (ret2 < 0) {
						if ((ret2 == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) && !Eof) {
							if (Video.StreamIndex >= 0)
								Video.VideoQ.PutNullPacket(pkt, Video.StreamIndex);
							if (!hasSeparateAudio && Audio.StreamIndex >= 0)
								Audio.AudioQ.PutNullPacket(pkt, Audio.StreamIndex);
							if (Subtitle.StreamIndex >= 0)
								Subtitle.SubtitleQ.PutNullPacket(pkt, Subtitle.StreamIndex);
							Eof = true;
						}
						if (ic->pb != null && ic->pb->error != 0)
							break;
						_continueReadThread.Wait(10);
						continue;
					}
					Eof = false;

					long streamStartTime = ic->streams[pkt->stream_index]->start_time;
					long pktTs           = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
					bool inRange = (pktTs - (streamStartTime != ffmpeg.AV_NOPTS_VALUE ? streamStartTime : 0))
						* ffmpeg.av_q2d(ic->streams[pkt->stream_index]->time_base) >= 0;

					if (!hasSeparateAudio && pkt->stream_index == Audio.StreamIndex && inRange)
						Audio.AudioQ.Put(pkt);
					else if (pkt->stream_index == Video.StreamIndex && inRange
						&& (Video.StreamPtr->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
						Video.VideoQ.Put(pkt);
					else if (pkt->stream_index == Subtitle.StreamIndex && inRange)
						Subtitle.SubtitleQ.Put(pkt);
					else
						ffmpeg.av_packet_unref(pkt);
				}
			} finally {
				if (Ic == null && ic != null)
					ffmpeg.avformat_close_input(&ic);
				ffmpeg.av_packet_free(&pkt);
			}
		}

		// ─────────────────────────────────────────────────────────────────
		// audio read thread — opens a separate audio-only input (e.g. YouTube
		// DASH audio stream) and feeds Audio.AudioQ.
		// ─────────────────────────────────────────────────────────────────
		private void StartAudioReadThread() {
			_audioReadTid = new Thread(AudioReadThread) { IsBackground = true, Name = "ffplay_read_audio" };
			_audioReadTid.Start();
		}

		private void AudioReadThread() {
			AVFormatContext* ic  = null;
			AVPacket*        pkt = ffmpeg.av_packet_alloc();
			if (pkt == null)
				return;

			try {
				ic = ffmpeg.avformat_alloc_context();
				if (ic == null)
					return;

				var self    = GCHandle.Alloc(this);
				var selfPtr = (void*)GCHandle.ToIntPtr(self);
				var cbDelegate = new AVIOInterruptCB_callback(opaque => {
					var h = GCHandle.FromIntPtr((IntPtr)opaque);
					return h.IsAllocated && ((PlayerState)h.Target).AbortRequest ? 1 : 0;
				});
				var cb = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(cbDelegate) };
				ic->interrupt_callback = new AVIOInterruptCB { callback = cb, opaque = selfPtr };

				int err = ffmpeg.avformat_open_input(&ic, AudioUrl, null, null);
				if (err < 0) {
					Debug.LogError($"[FFplay] Cannot open audio {AudioUrl}: {Helper.AvErr(err)}");
					self.Free();
					return;
				}

				_icAudio = ic;

				err = ffmpeg.avformat_find_stream_info(ic, null);
				if (err < 0)
					Debug.LogWarning($"[FFplay] {AudioUrl}: could not find codec parameters");

				int audioIndex = ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
				if (audioIndex >= 0)
					StreamComponentOpen(audioIndex, ic);

				if (Audio.StreamIndex < 0) {
					Debug.LogError($"[FFplay] Failed to open audio stream in {AudioUrl}");
					return;
				}

				for (;;) {
					if (AbortRequest)
						break;

					if (AudioSeekReq) {
						int r2 = ffmpeg.avformat_seek_file(ic, -1, long.MinValue, SeekPos, SeekPos, 0);
						if (r2 < 0)
							Debug.LogError($"[FFplay] audio seek error: {Helper.AvErr(r2)}");
						else
							Audio.AudioQ.Flush();
						AudioSeekReq = false;
					}

					if (Audio.AudioQ.Size > Constants.MAX_QUEUE_SIZE
						|| StreamHasEnoughPackets(Audio.StreamPtr, Audio.StreamIndex, Audio.AudioQ)) {
						_continueReadThread.Wait(10);
						continue;
					}

					int ret2 = ffmpeg.av_read_frame(ic, pkt);
					if (ret2 < 0) {
						if (ret2 == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) {
							Audio.AudioQ.PutNullPacket(pkt, Audio.StreamIndex);
							_continueReadThread.Wait(10);
							continue;
						}
						if (ic->pb != null && ic->pb->error != 0)
							break;
						_continueReadThread.Wait(10);
						continue;
					}

					if (pkt->stream_index == Audio.StreamIndex)
						Audio.AudioQ.Put(pkt);
					else
						ffmpeg.av_packet_unref(pkt);
				}
			} finally {
				if (_icAudio == null && ic != null)
					ffmpeg.avformat_close_input(&ic);
				ffmpeg.av_packet_free(&pkt);
			}
		}

		private void SignalQuit()
			=> OnVideoFrameReady = null; // Controller will notice null

		// ─────────────────────────────────────────────────────────────────
		// Dispose / stream_close
		// ─────────────────────────────────────────────────────────────────
		public void Dispose() {
			AbortRequest = true;
			_continueReadThread.Release(); // unblock any Wait() immediately
			_continueReadThread.Release(); // also wake a possible audio read thread
			ReadTid?.Join();
			_audioReadTid?.Join();

			if (Audio.StreamIndex >= 0)
				StreamComponentClose(Audio.StreamIndex, _icAudio != null ? _icAudio : Ic);
			if (Video.StreamIndex >= 0)
				StreamComponentClose(Video.StreamIndex, Ic);
			if (Subtitle.StreamIndex >= 0)
				StreamComponentClose(Subtitle.StreamIndex, Ic);

			if (Ic != null) {
				var ic = Ic;
				ffmpeg.avformat_close_input(&ic);
				Ic = null;
			}
			if (_icAudio != null) {
				var ic2 = _icAudio;
				ffmpeg.avformat_close_input(&ic2);
				_icAudio = null;
			}
			Video.VideoQ.Dispose();
			Audio.AudioQ.Dispose();
			Subtitle.SubtitleQ.Dispose();
			Video.PictQ.Dispose();
			Audio.SampQ.Dispose();
			Subtitle.SubpQ.Dispose();
			_continueReadThread.Dispose();
		}
	}


	// ─────────────────────────────────────────────────────────────────────────
	// FFplayPlayer — Unity Controller (MonoBehaviour)
	// Only this class touches Unity APIs. VideoState is pure logic.
	// ─────────────────────────────────────────────────────────────────────────

}