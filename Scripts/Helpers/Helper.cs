using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;

namespace Nox.FFmpeg.Helpers {
	public static class Helper {
		public static unsafe string ErrorToString(int error) {
			const int bufferSize = 1024;
			var       buffer     = stackalloc byte[ bufferSize ];
			ffmpeg.av_strerror(error, buffer, bufferSize);
			var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
			return message;
		}

		public static int ThrowExceptionIfError(this int error) {
			if (error < 0)
				throw new ApplicationException(ErrorToString(error));
			return error;
		}

		public static void AbortDecoder(Decoder d, FrameQueue fq) {
			d.Queue.Abort();
			fq.Signal();
			d.DecoderTid?.Join();
			d.DecoderTid = null;
			d.Queue.Flush();
		}
		
		// ─────────────────────────────────────────────────────────────────
		// stream_has_enough_packets
		// ─────────────────────────────────────────────────────────────────
		public static unsafe bool StreamHasEnoughPackets(AVStream* stream, int index, PacketQueue queue)
			=> index < 0
				|| queue.AbortRequest
				|| stream == null
				|| (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0
				|| (queue.NbPackets > Constants.MIN_FRAMES
					&& (queue.Duration == 0 || ffmpeg.av_q2d(stream->time_base) * queue.Duration > 1.0));

	}
}