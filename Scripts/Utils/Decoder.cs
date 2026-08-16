using System;
using System.Threading;
using FFmpeg.AutoGen;
using UnityEngine;

namespace Nox.FFmpeg.Utils {

	// ─────────────────────────────────────────────────────────────────────────
	// Decoder — mirrors ffplay.c Decoder
	// ─────────────────────────────────────────────────────────────────────────
	public unsafe class Decoder : IDisposable {
		public AVPacket* Pkt;
		public PacketQueue Queue;
		public AVCodecContext* Avctx;
		public int PktSerial = -1;
		public int Finished;
		public bool PacketPending;
		public long StartPts = ffmpeg.AV_NOPTS_VALUE;
		public AVRational StartPtsTb;
		public long NextPts = ffmpeg.AV_NOPTS_VALUE;
		public AVRational NextPtsTb;
		public Thread DecoderTid;

		// empty_queue_cond equivalent: Action called when queue empty
		public readonly Action EmptyQueueSignal;

		public Decoder(AVCodecContext* avctx, PacketQueue queue, Action emptyQueueSignal) {
			Avctx            = avctx;
			Queue            = queue;
			EmptyQueueSignal = emptyQueueSignal;
			Pkt              = ffmpeg.av_packet_alloc();
		}

		// decoder_decode_frame — core loop identical to ffplay.c
		// Returns: -1 abort, 0 got frame / subtitle, >0 (1) = need more input
		public int DecodeFrame(AVFrame* frame, AVSubtitle* sub) {
			int ret = ffmpeg.AVERROR(ffmpeg.EAGAIN);

			for (;;) {
				if (Queue.Serial == PktSerial) {
					do {
						if (Queue.AbortRequest)
							return -1;

						switch (Avctx->codec_type) {
							case AVMediaType.AVMEDIA_TYPE_VIDEO:
								ret = ffmpeg.avcodec_receive_frame(Avctx, frame);
								if (ret >= 0)
									frame->pts = frame->best_effort_timestamp;
								break;

							case AVMediaType.AVMEDIA_TYPE_AUDIO:
								ret = ffmpeg.avcodec_receive_frame(Avctx, frame);
								if (ret >= 0) {
									AVRational tb = new AVRational { num = 1, den = frame->sample_rate };
									if (frame->pts != ffmpeg.AV_NOPTS_VALUE)
										frame->pts = ffmpeg.av_rescale_q(frame->pts, Avctx->pkt_timebase, tb);
									else if (NextPts != ffmpeg.AV_NOPTS_VALUE)
										frame->pts = ffmpeg.av_rescale_q(NextPts, NextPtsTb, tb);
									if (frame->pts != ffmpeg.AV_NOPTS_VALUE) {
										NextPts   = frame->pts + frame->nb_samples;
										NextPtsTb = tb;
									}
								}
								break;
						}

						if (ret == ffmpeg.AVERROR_EOF) {
							Finished = PktSerial;
							ffmpeg.avcodec_flush_buffers(Avctx);
							return 0;
						}
						if (ret >= 0)
							return 1;
					} while (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN));
				}

				// Need more packets
				do {
					if (Queue.NbPackets == 0)
						EmptyQueueSignal?.Invoke();

					if (PacketPending) {
						PacketPending = false;
					} else {
						int oldSerial = PktSerial;
						if (Queue.Get(Pkt, true, out PktSerial) < 0)
							return -1;
						if (oldSerial != PktSerial) {
							ffmpeg.avcodec_flush_buffers(Avctx);
							Finished  = 0;
							NextPts   = StartPts;
							NextPtsTb = StartPtsTb;
						}
					}

					if (Queue.Serial == PktSerial)
						break;
					ffmpeg.av_packet_unref(Pkt);
				} while (true);

				// subtitle path (no filter graph in this Unity port)
				if (Avctx->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE && sub != null) {
					int gotFrame = 0;
					ret = ffmpeg.avcodec_decode_subtitle2(Avctx, sub, &gotFrame, Pkt);
					if (ret < 0)
						ret = ffmpeg.AVERROR(ffmpeg.EAGAIN);
					else {
						if (gotFrame != 0 && Pkt->data == null)
							PacketPending = true;
						ret = gotFrame != 0 ? 0 : (Pkt->data != null ? ffmpeg.AVERROR(ffmpeg.EAGAIN) : ffmpeg.AVERROR_EOF);
					}
					ffmpeg.av_packet_unref(Pkt);
				} else {
					if (ffmpeg.avcodec_send_packet(Avctx, Pkt) == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
						Debug.LogError("[FFplay] Receive_frame and send_packet both returned EAGAIN — API violation.");
						PacketPending = true;
					} else {
						ffmpeg.av_packet_unref(Pkt);
					}
				}
			}
		}

		public void Dispose() {
			var p = Pkt;
			ffmpeg.av_packet_free(&p);
			var ctx = Avctx;
			ffmpeg.avcodec_free_context(&ctx);
		}
	}
}