using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using UnityEngine;
using UnityEngine.Events;
using Helper = Nox.FFmpeg.Helpers.Helper;
using System.Drawing;

namespace Nox.FFmpeg.Handlers {
	/// Decodes video frames and converts them to a <see cref="Texture2D"/>.
	public unsafe sealed class VideoHandler : IHandler {

		public VideoHandler(PlayerState state) : base(state) {
			Frames = new FrameQueue(Packets, Constants.VIDEO_PICTURE_QUEUE_SIZE, true);
			Clock = new Clock(() => Packets.GetSerial());
		}

		public override StreamType Type 
			=> StreamType.Video;

		public override AVMediaType[] MediaTypes
			=> new[] { AVMediaType.AVMEDIA_TYPE_VIDEO };

		public Action<IntPtr> OnVideoFrameReady; // IntPtr to AVFrame*


		// ── video refresh timing ──────────────────────────────────────────
		public double FrameTimer;
		public double FrameLastReturnedTime;
		public double FrameLastFilterDelay;
		public int FrameDropsEarly;
		public int FrameDropsLate;
		public bool ForceRefresh;

		public override void Open(int index, AVFormatContext* ic, AVCodecContext* avctx) {
			StreamIndex = index;
			StreamPtr   = ic->streams[index];
			Decoder      = new Decoder(avctx, Packets, () => State.ContinueReadThread.Release());
			Packets.Start();
			Decoder.DecoderTid = new Thread(VideoThread) { 
				IsBackground = true, 
				Name = "ffplay_video"
			};
			Decoder.DecoderTid.Start();
		}

		public override void Close() {
			Helper.AbortDecoder(Decoder, Frames);
			Decoder.Dispose();
			Decoder      = null;
			StreamIndex = -1;
			StreamPtr   = null;
		}

		internal override void OnPause(bool paused) {
			if (paused) {
				FrameTimer += ffmpeg.av_gettime_relative() / 1_000_000.0 - Clock.LastUpdated;
				if (State.ReadPauseReturn != ffmpeg.AVERROR(38 /* ENOSYS */))
					Clock.Paused = false;
				Clock.Set(Clock.Get(), Clock.Serial);
			}
			base.OnPause(paused);
		}

		// ─────────────────────────────────────────────────────────────────
		// video_thread
		// ─────────────────────────────────────────────────────────────────
		private void VideoThread() {
			AVFrame* frame = ffmpeg.av_frame_alloc();
			if (frame == null)
				return;

			AVStream* videoSt   = StreamPtr;
			AVRational tb        = videoSt->time_base;
			AVRational frameRate = ffmpeg.av_guess_frame_rate(State.Context, videoSt, null);

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

					ret = QueuePicture(frame, pts, duration, frame->pts, Decoder.PktSerial);
					ffmpeg.av_frame_unref(frame);
					if (Packets.Serial != Decoder.PktSerial)
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
			int gotPicture = Decoder.DecodeFrame(frame, null);
			if (gotPicture < 0)
				return -1;
			if (gotPicture == 0)
				return 0; // EOF

			var videoSt = StreamPtr;
			double dpts = frame->pts != ffmpeg.AV_NOPTS_VALUE
				? ffmpeg.av_q2d(videoSt->time_base) * frame->pts : double.NaN;

			frame->sample_aspect_ratio = ffmpeg.av_guess_sample_aspect_ratio(State.Context, videoSt, frame);

			// framedrop early
			if (!double.IsNaN(dpts)) {
				double diff = dpts - State.MasterClock;
				if (!double.IsNaN(diff) && Math.Abs(diff) < Constants.AV_NOSYNC_THRESHOLD
					&& diff < 0
					&& Decoder.PktSerial == Clock.Serial
					&& Packets.NbPackets != 0) {
					FrameDropsEarly++;
					ffmpeg.av_frame_unref(frame);
					return 0;
				}
			}
			return 1;
		}

		// queue_picture
		private int QueuePicture(AVFrame* srcFrame, double pts, double duration, long pos, int serial) {
			Frame vp = Frames.PeekWritable();
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
			Frames.Push();
			return 0;
		}

		// ─────────────────────────────────────────────────────────────────
		// video_refresh — called every REFRESH_RATE from the Controller Update
		// Returns: remaining_time suggestion
		// ─────────────────────────────────────────────────────────────────
		public double VideoRefresh(double remainingTime) {
			if (!State.Paused && State.MasterSyncType == Constants.AV_SYNC_EXTERNAL_CLOCK && State.Realtime)
				State.CheckExternalClockSpeed();

			if (StreamIndex < 0)
				return remainingTime;

		retry:
			if (Frames.NbRemaining() == 0)
				return remainingTime;

			Frame lastvp = Frames.PeekLast();
			Frame vp     = Frames.Peek();

			if (vp.Serial != Packets.Serial) {
				Frames.Next();
				goto retry;
			}
			if (lastvp.Serial != vp.Serial)
				FrameTimer = ffmpeg.av_gettime_relative() / 1_000_000.0;

			if (State.Paused)
				goto display;

			double lastDuration = State.VpDuration(lastvp, vp);
			double delay        = ComputeTargetDelay(lastDuration);
			double time         = ffmpeg.av_gettime_relative() / 1_000_000.0;

			if (time < FrameTimer + delay)
				return Math.Min(FrameTimer + delay - time, remainingTime);

			FrameTimer += delay;
			if (delay > 0 && time - FrameTimer > Constants.AV_SYNC_THRESHOLD_MAX)
				FrameTimer = time;

			if (!double.IsNaN(vp.Pts))
				UpdateVideoPts(vp.Pts, vp.Serial);

			if (Frames.NbRemaining() > 1) {
				Frame  nextvp = Frames.PeekNext();
				double dur    = State.VpDuration(vp, nextvp);
				if (State.Step == 0 && time > FrameTimer + dur) {
					FrameDropsLate++;
					Frames.Next();
					goto retry;
				}
			}

			Frames.Next();
			ForceRefresh = true;
			if (State.Step != 0 && !State.Paused)
				State.StreamTogglePause();

		display:
			if (ForceRefresh && Frames.NbRemaining() > 0)
				OnVideoFrameReady?.Invoke((IntPtr)Frames.PeekLast().AVFrame);

			ForceRefresh = false;
			return remainingTime;
		}

		// update_video_pts
		private void UpdateVideoPts(double pts, int serial) {
			Clock.Set(pts, serial);
			State.ExternalClock.SyncToSlave(Clock);
		}

		// compute_target_delay
		public double ComputeTargetDelay(double delay) {
			if (State.MasterSyncType == Constants.AV_SYNC_VIDEO_MASTER)
				return delay;
			double diff = Clock.Get() - State.MasterClock;
			double syncThr = Math.Max(Constants.AV_SYNC_THRESHOLD_MIN,
				Math.Min(Constants.AV_SYNC_THRESHOLD_MAX, delay));
			if (!double.IsNaN(diff) && Math.Abs(diff) < State.MaxFrameDuration) {
				if (diff <= -syncThr)
					delay = Math.Max(0, delay + diff);
				else if (diff >= syncThr && delay > Constants.AV_SYNC_FRAMEDUP_THRESHOLD)
					delay += diff;
				else if (diff >= syncThr)
					delay *= 2;
			}
			return delay;
		}

		public Texture2D Frame { get; private set; }
		public UnityEvent<Texture2D> OnFrame { get; } = new();

		public Vector2Int Resolution { get; private set; } = Vector2Int.zero;
		public UnityEvent<Vector2Int> OnResolution { get; } = new();

		// ── FFmpeg demux/decode state (owned by this handler) ────────────
		public PacketQueue Packets { get; } = new();
		public FrameQueue  Frames  { get; }

		public override bool HasEnded
			=> StreamPtr == null || (Decoder != null && Decoder.Finished == Packets.Serial && Frames.NbRemaining() == 0);

		public override bool HasEnoughPackets
			=> StreamIndex < 0 || Packets.NbPackets >= Constants.MIN_FRAMES / 4;

		private readonly Player player;

		private Converter _converter;
		private int _convW, _convH;
		private AVPixelFormat _convFormat;
		private byte[] _rgb;

		public override void Start() {
			IsRunning = true;
			OnVideoFrameReady = HandleFrame;
		}

		public override void Stop() {
			IsRunning = false;
			OnSignalQuit();
			_converter?.Dispose();
			_converter = null;
			if (Frame) {
				UnityEngine.Object.Destroy(Frame);
				Frame = null;
			}
		}

		public override void OnSignalQuit()
			=> OnVideoFrameReady = null; // Controller will notice null

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
					new Size(w, h), fmt,
					new Size(w, h), AVPixelFormat.AV_PIX_FMT_RGB24
				);
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
				var res = new Vector2Int(w, h);
				if (Resolution != res) {
					Resolution = res;
					OnResolution.Invoke(res);
				}
			}
			Frame.LoadRawTextureData(buf);
			Frame.Apply(false);
			OnFrame.Invoke(Frame);
		}
	}
}