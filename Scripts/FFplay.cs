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
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Nox.FFmpeg.Handlers;
using UnityEngine;
using Helper = Nox.FFmpeg.Helpers.Helper;
using System.Linq;
using UnityEngine.Events;

namespace Nox.FFmpeg {
	

	// ─────────────────────────────────────────────────────────────────────────
	// VideoState — the Model; mirrors VideoState in ffplay.c
	// No SDL, no rendering: raises events consumed by FFplayPlayer (Controller)
	// ─────────────────────────────────────────────────────────────────────────
	public unsafe class PlayerState : IDisposable {
		// ── streams (URLs live on the IStreams; indices/AVStream* on the handlers) ──
		public AVFormatContext* Context;
		public bool Realtime;
		public bool Eof;
		public bool Loop;

		// ── external clock (stream clocks live on their handlers) ─────────
		public Clock ExternalClock;

		// ── state ─────────────────────────────────────────────────────────
		public bool AbortRequest;
		public bool Paused,			LastPaused;
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

		// ── threading ─────────────────────────────────────────────────────
		public Thread ReadTid;
		private Thread _audioReadTid;
		public readonly SemaphoreSlim ContinueReadThread = new(0);
		private AVFormatContext* _icAudio;

		// ── Controller callbacks (Unity output) ───────────────────────────
		/// Called from VideoState (Model) when a video frame is ready.
		/// The consumer (Controller) converts it to Texture2D on the main thread.
		/// Called when a block of float PCM (stereo, target sample rate) is ready.
		public Action<float[], int, int> OnAudioSamplesReady; // data, channels, freq

		// ── typed handlers & streams (moved from the Player controller) ──
		public IStream[]  Streams  = Array.Empty<IStream>();
		public IHandler[] Handlers = Array.Empty<IHandler>();

		public UnityEvent OnEndReached = new();

		public AudioHandler Audio   
			=> GetHandler<AudioHandler>();
		public VideoHandler Video    
			=> GetHandler<VideoHandler>();
		public SubtitleHandler Subtitle 
			=> GetHandler<SubtitleHandler>();

		public T GetHandler<T>() where T : IHandler
			=> Array.Find(Handlers, h => h is T) as T;

		/// Main input URL (the video / combined stream).
		public IStream VideoUrl 
			=> GetStreamUrl(StreamType.Video);
		/// Separate audio-only input URL, or null when audio lives in the main input.
		public IStream AudioUrl 
			=> GetStreamUrl(StreamType.Audio);

		private IStream GetStreamUrl(StreamType type) {
			for (int i = 0; i < Streams.Length; i++)
				if ((Streams[i].Type & type) != 0 && !string.IsNullOrEmpty(Streams[i].Url))
					return Streams[i];
			return null;
		}

		// ─────────────────────────────────────────────────────────────────
		// init_clock / stream_open equivalent
		// ─────────────────────────────────────────────────────────────────
		public PlayerState() 
			=> ExternalClock = new Clock(() => ExternalClock?.Serial ?? -1); // self-referential like ffplay.c
		
		public void Update() {
			// When not looping, pause once playback reaches the end so the
			// clock/counter stop instead of running past the duration.
			if (!Loop && Eof && !Paused && HasReachedEnd)
				OnEndReached.Invoke();

			// Drive video_refresh every frame; let it decide internally when to display
			Video.VideoRefresh(Constants.REFRESH_RATE);
		}

		// ─────────────────────────────────────────────────────────────────
		// get_master_sync_type
		// ─────────────────────────────────────────────────────────────────
		public int MasterSyncType
			=> AvSyncType switch {
				Constants.AV_SYNC_VIDEO_MASTER => Video.StreamIndex >= 0 
					? Constants.AV_SYNC_VIDEO_MASTER 
					: Constants.AV_SYNC_AUDIO_MASTER,
				Constants.AV_SYNC_AUDIO_MASTER => Audio.StreamIndex >= 0 
					? Constants.AV_SYNC_AUDIO_MASTER 
					: Constants.AV_SYNC_EXTERNAL_CLOCK,
				_                              => Constants.AV_SYNC_EXTERNAL_CLOCK,
			};

		// get_master_clock
		public double MasterClock
			=> MasterSyncType switch {
				Constants.AV_SYNC_VIDEO_MASTER => Video.Clock.Get(),
				Constants.AV_SYNC_AUDIO_MASTER => Audio.Clock.Get(),
				_                              => ExternalClock.Get(),
			};

		public bool HasReachedEnd
			=> Handlers.All(h => h.HasEnded);

		// check_external_clock_speed
		public void CheckExternalClockSpeed() {
			if ((Video.StreamIndex >= 0 && Video.Packets.NbPackets <= Constants.EXTERNAL_CLOCK_MIN_FRAMES) ||
				(Audio.StreamIndex >= 0 && Audio.Packets.NbPackets <= Constants.EXTERNAL_CLOCK_MIN_FRAMES))
				ExternalClock.SetSpeed(Math.Max(Constants.EXTERNAL_CLOCK_SPEED_MIN,
					ExternalClock.Speed - Constants.EXTERNAL_CLOCK_SPEED_STEP));
			else if ((Video.StreamIndex < 0 || Video.Packets.NbPackets > Constants.EXTERNAL_CLOCK_MAX_FRAMES) &&
				(Audio.StreamIndex < 0 || Audio.Packets.NbPackets > Constants.EXTERNAL_CLOCK_MAX_FRAMES))
				ExternalClock.SetSpeed(Math.Min(Constants.EXTERNAL_CLOCK_SPEED_MAX,
					ExternalClock.Speed + Constants.EXTERNAL_CLOCK_SPEED_STEP));
			else {
				double s = ExternalClock.Speed;
				if (s != 1.0)
					ExternalClock.SetSpeed(s + Constants.EXTERNAL_CLOCK_SPEED_STEP * (1.0 - s) / Math.Abs(1.0 - s));
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
			ContinueReadThread.Release();
		}

		// stream_toggle_pause
		public void StreamTogglePause() {
			ExternalClock.Set(ExternalClock.Get(), ExternalClock.Serial);
			ExternalClock.Paused = !Paused;
			foreach (var h in Handlers)
				h.OnPause(ExternalClock.Paused);
			Paused = ExternalClock.Paused;
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

		public double VpDuration(Frame vp, Frame nextvp) {
			if (vp.Serial != nextvp.Serial)
				return 0.0;
			double d = nextvp.Pts - vp.Pts;
			if (double.IsNaN(d) || d <= 0 || d > MaxFrameDuration)
				return vp.Duration;
			return d;
		}

		// ─────────────────────────────────────────────────────────────────
		// stream_component_open
		// ─────────────────────────────────────────────────────────────────
		public int OpenStream(int index, AVFormatContext* ic) {
			if (index < 0 || index >= (int)ic->nb_streams)
				return -1;

			AVCodecContext* avctx = ffmpeg.avcodec_alloc_context3(null);
			if (avctx == null)
				return ffmpeg.AVERROR(ffmpeg.ENOMEM);

			int ret = ffmpeg.avcodec_parameters_to_context(avctx, ic->streams[index]->codecpar);
			if (ret < 0)
				goto fail;

			avctx->pkt_timebase = ic->streams[index]->time_base;
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

			ic->streams[index]->discard = AVDiscard.AVDISCARD_DEFAULT;

			AVMediaType codecType = avctx->codec_type;
			foreach (var h in Handlers)
				if (Array.Exists(h.MediaTypes, t => t == codecType)) {
					h.Open(index, ic, avctx);
					return 0;
				}

		fail:
			ffmpeg.avcodec_free_context(&avctx);
			return ret;
		}

		// stream_component_close
		public void CloseStream(int index, AVFormatContext* ic) {
			if (index < 0 || index >= (int)ic->nb_streams)
				return;
			var par = ic->streams[index]->codecpar;

			AVMediaType codecType = par->codec_type;
			foreach (var h in Handlers)
				if (Array.Exists(h.MediaTypes, t => t == codecType)) {
					h.Close();
					break;
				}

			ic->streams[index]->discard = AVDiscard.AVDISCARD_ALL;
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

				var videoUrl = VideoUrl ?? (Streams.Length > 0 ? Streams[0] : null);
				if (videoUrl == null || string.IsNullOrEmpty(videoUrl.Url)) {
					Debug.LogError("[FFplay] No video URL to open.");
					SignalQuit();
					return;
				}
				bool hasSeparateAudio = AudioUrl != null
					&& !string.IsNullOrEmpty(AudioUrl.Url)
					&& !string.Equals(AudioUrl.Url, videoUrl.Url, StringComparison.Ordinal);

				var opts = Helper.BuildHeaders(videoUrl.Url, videoUrl.Headers);
				int err = ffmpeg.avformat_open_input(&ic, videoUrl.Url, null, &opts);
				ffmpeg.av_dict_free(&opts);
				if (err < 0) {
					Debug.LogError($"[FFplay] Cannot open {videoUrl.Url}: {Helper.ErrorToString(err)}");
					self.Free();
					SignalQuit();
					return;
				}

				Context = ic;

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
					OpenStream(stIndex[(int)AVMediaType.AVMEDIA_TYPE_AUDIO], ic);

				if (stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO] >= 0)
					OpenStream(stIndex[(int)AVMediaType.AVMEDIA_TYPE_VIDEO], ic);

				if (stIndex[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE] >= 0)
					OpenStream(stIndex[(int)AVMediaType.AVMEDIA_TYPE_SUBTITLE], ic);

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
							Debug.LogError($"[FFplay] seek error: {Helper.ErrorToString(r2)}");
						else {
							if (Audio.StreamIndex >= 0)
								Audio.Packets.Flush();
							if (Subtitle.StreamIndex >= 0)
								Subtitle.Packets.Flush();
							if (Video.StreamIndex >= 0)
								Video.Packets.Flush();
							ExternalClock.Set((SeekFlags & ffmpeg.AVSEEK_FLAG_BYTE) != 0
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
								Video.Packets.Put(pkt);
								Video.Packets.PutNullPacket(pkt, Video.StreamIndex);
							}
						}
						QueueAttachmentsReq = false;
					}

					// buffer full — wait
					bool enoughPackets =
						Helper.StreamHasEnoughPackets(Audio.StreamPtr, Audio.StreamIndex, Audio.Packets) &&
						Helper.StreamHasEnoughPackets(Video.StreamPtr, Video.StreamIndex, Video.Packets) &&
						Helper.StreamHasEnoughPackets(Subtitle.StreamPtr, Subtitle.StreamIndex, Subtitle.Packets);
					if (Audio.Packets.Size + Video.Packets.Size + Subtitle.Packets.Size > Constants.MAX_QUEUE_SIZE || enoughPackets) {
						ContinueReadThread.Wait(10);
						continue;
					}

					// auto-loop when finished (only when looping is enabled)
					if (Loop
						&& !Paused
						&& (Audio.StreamPtr == null || (Audio.Decoder != null && Audio.Decoder.Finished == Audio.Packets.Serial && Audio.Frames.NbRemaining() == 0))
						&& (Video.StreamPtr == null || (Video.Decoder != null && Video.Decoder.Finished == Video.Packets.Serial && Video.Frames.NbRemaining() == 0))) {
						StreamSeek(0, 0, false); // loop
						continue;
					}

					int ret2 = ffmpeg.av_read_frame(ic, pkt);
					if (ret2 < 0) {
						if ((ret2 == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) && !Eof) {
							if (Video.StreamIndex >= 0)
								Video.Packets.PutNullPacket(pkt, Video.StreamIndex);
							if (!hasSeparateAudio && Audio.StreamIndex >= 0)
								Audio.Packets.PutNullPacket(pkt, Audio.StreamIndex);
							if (Subtitle.StreamIndex >= 0)
								Subtitle.Packets.PutNullPacket(pkt, Subtitle.StreamIndex);
							Eof = true;
						}
						if (ic->pb != null && ic->pb->error != 0)
							break;
						ContinueReadThread.Wait(10);
						continue;
					}
					Eof = false;

					long streamStartTime = ic->streams[pkt->stream_index]->start_time;
					long pktTs           = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
					bool inRange = (pktTs - (streamStartTime != ffmpeg.AV_NOPTS_VALUE ? streamStartTime : 0))
						* ffmpeg.av_q2d(ic->streams[pkt->stream_index]->time_base) >= 0;

					if (!hasSeparateAudio && pkt->stream_index == Audio.StreamIndex && inRange)
						Audio.Packets.Put(pkt);
					else if (pkt->stream_index == Video.StreamIndex && inRange
						&& (Video.StreamPtr->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
						Video.Packets.Put(pkt);
					else if (pkt->stream_index == Subtitle.StreamIndex && inRange)
						Subtitle.Packets.Put(pkt);
					else
						ffmpeg.av_packet_unref(pkt);
				}
			} finally {
				if (Context == null && ic != null)
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

				var opts = Helper.BuildHeaders(AudioUrl.Url, AudioUrl.Headers);
				int err = ffmpeg.avformat_open_input(&ic, AudioUrl.Url, null, &opts);
				ffmpeg.av_dict_free(&opts);
				if (err < 0) {
					Debug.LogError($"[FFplay] Cannot open audio {AudioUrl}: {Helper.ErrorToString(err)}");
					self.Free();
					return;
				}

				_icAudio = ic;

				err = ffmpeg.avformat_find_stream_info(ic, null);
				if (err < 0)
					Debug.LogWarning($"[FFplay] {AudioUrl}: could not find codec parameters");

				int audioIndex = ffmpeg.av_find_best_stream(ic, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
				if (audioIndex >= 0)
					OpenStream(audioIndex, ic);

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
							Debug.LogError($"[FFplay] audio seek error: {Helper.ErrorToString(r2)}");
						else
							Audio.Packets.Flush();
						AudioSeekReq = false;
					}

					if (Audio.Packets.Size > Constants.MAX_QUEUE_SIZE
						|| Helper.StreamHasEnoughPackets(Audio.StreamPtr, Audio.StreamIndex, Audio.Packets)) {
						ContinueReadThread.Wait(10);
						continue;
					}

					int ret2 = ffmpeg.av_read_frame(ic, pkt);
					if (ret2 < 0) {
						if (ret2 == ffmpeg.AVERROR_EOF || ffmpeg.avio_feof(ic->pb) != 0) {
							Audio.Packets.PutNullPacket(pkt, Audio.StreamIndex);
							ContinueReadThread.Wait(10);
							continue;
						}
						if (ic->pb != null && ic->pb->error != 0)
							break;
						ContinueReadThread.Wait(10);
						continue;
					}

					if (pkt->stream_index == Audio.StreamIndex)
						Audio.Packets.Put(pkt);
					else
						ffmpeg.av_packet_unref(pkt);
				}
			} finally {
				if (_icAudio == null && ic != null)
					ffmpeg.avformat_close_input(&ic);
				ffmpeg.av_packet_free(&pkt);
			}
		}

		private void SignalQuit() {
			foreach(var h in Handlers)
				h.OnSignalQuit();
		}

		// ─────────────────────────────────────────────────────────────────
		// Dispose / stream_close
		// ─────────────────────────────────────────────────────────────────
		public void Dispose() {
			AbortRequest = true;
			ContinueReadThread.Release(); // unblock any Wait() immediately
			ContinueReadThread.Release(); // also wake a possible audio read thread
			ReadTid?.Join();
			_audioReadTid?.Join();

			if (Audio.StreamIndex >= 0)
				CloseStream(Audio.StreamIndex, _icAudio != null ? _icAudio : Context);
			if (Video.StreamIndex >= 0)
				CloseStream(Video.StreamIndex, Context);
			if (Subtitle.StreamIndex >= 0)
				CloseStream(Subtitle.StreamIndex, Context);

			if (Context != null) {
				var ic = Context;
				ffmpeg.avformat_close_input(&ic);
				Context = null;
			}
			if (_icAudio != null) {
				var ic2 = _icAudio;
				ffmpeg.avformat_close_input(&ic2);
				_icAudio = null;
			}

			foreach(var h in Handlers)
				h.Dispose();

			ContinueReadThread.Dispose();
		}
	}


	// ─────────────────────────────────────────────────────────────────────────
	// FFplayPlayer — Unity Controller (MonoBehaviour)
	// Only this class touches Unity APIs. VideoState is pure logic.
	// ─────────────────────────────────────────────────────────────────────────

}