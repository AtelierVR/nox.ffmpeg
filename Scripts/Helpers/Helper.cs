using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;
using System.Collections.Generic;

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

		// ─────────────────────────────────────────────────────────────────
		// build_http_options — YouTube (googlevideo / manifest.googlevideo.com)
		// URLs require a browser User-Agent + Referer. Without them FFmpeg's
		// HTTP client is served a 403 (or an HTML error body the HLS demuxer
		// cannot parse as a playlist → "Invalid data found when processing input").
		// ─────────────────────────────────────────────────────────────────
		public static unsafe AVDictionary* BuildHeaders(string url, Dictionary<string, string> headers) {
			AVDictionary* opts = null;

			// Merge caller-provided headers (yt-dlp http_headers) case-insensitively.
			var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (headers != null)
				foreach (var h in headers)
					if (!string.IsNullOrWhiteSpace(h.Key))
						merged[h.Key] = h.Value;

			if (merged.Count == 0)
				return null;

			var sb = new System.Text.StringBuilder();
			foreach (var h in merged)
				sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
			ffmpeg.av_dict_set(&opts, "headers", sb.ToString(), 0);
			return opts;
		}
	}
}