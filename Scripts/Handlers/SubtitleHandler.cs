using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Helper = Nox.FFmpeg.Helpers.Helper;

namespace Nox.FFmpeg.Handlers {
	/// Handles subtitle packets/frames. No rendering in this port.
	public unsafe sealed class SubtitleHandler : IHandler {

		public SubtitleHandler(PlayerState state) : base(state) {
			Frames = new FrameQueue(Packets, 16, false);
		}

		public override StreamType Type 
			=> StreamType.Subtitle;
		
		public override AVMediaType[] MediaTypes
			=> new[] { AVMediaType.AVMEDIA_TYPE_SUBTITLE };

		public override void Open(int index, AVFormatContext* ic, AVCodecContext* avctx) {
			StreamIndex = index;
			StreamPtr   = ic->streams[index];
			Decoder         = new Decoder(avctx, Packets, () => State.ContinueReadThread.Release());
			Packets.Start();
			Decoder.DecoderTid = new Thread(SubtitleThread) { IsBackground = true, Name = "ffplay_subtitle" };
			Decoder.DecoderTid.Start();
		}

		public override void Close() {
			Helper.AbortDecoder(Decoder, Frames);
			Decoder.Dispose();
			Decoder         = null;
			StreamIndex = -1;
			StreamPtr   = null;
		}
		
		// subtitle_thread (simplified — no rendering in this port)
		private void SubtitleThread() {
			for (;;) {
				Frame sp = Frames.PeekWritable();
				if (sp == null)
					return;

				AVSubtitle sub         = default;
				int        gotSubtitle = Decoder.DecodeFrame(null, &sub);
				if (gotSubtitle < 0)
					break;

				if (gotSubtitle != 0 && sub.format == 0) {
					sp.Pts    = sub.pts != ffmpeg.AV_NOPTS_VALUE ? sub.pts / (double)ffmpeg.AV_TIME_BASE : 0;
					sp.Serial = Decoder.PktSerial;
					sp.Width  = Decoder.Avctx->width;
					sp.Height = Decoder.Avctx->height;
					Frames.Push();
				} else if (gotSubtitle != 0)
					ffmpeg.avsubtitle_free(&sub);
			}
		}


		// ── FFmpeg demux/decode state (owned by this handler) ────────────
		public PacketQueue Packets { get; } = new();
		public FrameQueue  Frames     { get; }

		public override bool HasEnded
			=> StreamPtr == null || (Decoder != null && Decoder.Finished == Packets.Serial && Frames.NbRemaining() == 0);

		public override bool HasEnoughPackets => true;

		public override void Start() => IsRunning = true;
		public override void Stop()  => IsRunning = false;
	}
}
