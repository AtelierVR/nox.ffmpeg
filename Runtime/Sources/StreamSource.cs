using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;

namespace Nox.FFmpeg.Sources {
	public sealed unsafe class StreamSource : BaseSource {
		internal readonly AVIOContext* _pIOContext;
		internal readonly Stream _stream;
		internal readonly avio_alloc_context_read_packet read;
		internal readonly avio_alloc_context_seek seek;
		internal readonly GCHandle streamHandle;
		internal byte* bufferPtr = null;

		public StreamSource(Stream stream, uint bufferSize = 16_000_000) {
			if (stream == null)
				return;

			Debug.Log($"Opening stream {stream}");
			_stream   = stream;
			bufferPtr = (byte*)ffmpeg.av_malloc(bufferSize);
			read      = ReadPacketCallback;
			if (stream.CanSeek)
				seek = SeekPacketCallback;
			streamHandle = GCHandle.Alloc(_stream, GCHandleType.Normal);
			_pIOContext  = ffmpeg.avio_alloc_context(bufferPtr, (int)bufferSize, 0, GCHandle.ToIntPtr(streamHandle).ToPointer(), read, null, seek);

			_pFormatContext                       =  ffmpeg.avformat_alloc_context();
			_pFormatContext->flags                |= ffmpeg.AVFMT_FLAG_SHORTEST;
			_pFormatContext->max_interleave_delta =  100_000_000;
			_pFormatContext->pb                   =  _pIOContext;
			_pFormatContext->flags                |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
			_pFormatContext->avio_flags           =  ffmpeg.AVIO_FLAG_READ | ffmpeg.AVIO_FLAG_NONBLOCK;

			var pFormatContext = _pFormatContext;
			var url            = "some_dummy_filename";
			ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
			ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();

			_videoIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);

			_pPacket = ffmpeg.av_packet_alloc();
			IsValid  = true;
		}

		internal static int ReadPacketCallback(void* @opaque, byte* @buf, int @buf_size) {
			int ret    = ffmpeg.AVERROR_EOF;
			var handle = GCHandle.FromIntPtr((IntPtr)opaque);
			if (!handle.IsAllocated) {
				return ret;
			}
			var stream = (Stream)handle.Target;
			if (buf == null) {
				return ret;
			}
			var span = new Span<byte>(buf, buf_size);
			if (stream == null || !stream.CanRead) {
				return ret;
			}
			int count = stream.Read(span);
			return count == 0 ? ret : count;
		}

		internal static long SeekPacketCallback(void* @opaque, long @offset, int @whence) {
			int ret    = ffmpeg.AVERROR_EOF;
			var handle = GCHandle.FromIntPtr((IntPtr)opaque);
			if (!handle.IsAllocated) {
				return ret;
			}
			var stream = (Stream)handle.Target;
			if (stream == null || !stream.CanSeek) {
				return ret;
			}
			long idk = stream.Seek(offset, SeekOrigin.Begin);
			return idk;
		}

		public override void Dispose() {
			if (!IsValid)
				return;

			base.Dispose();

			var pIOContext = _pIOContext;
			if (pIOContext != null)
				ffmpeg.avio_context_free(&pIOContext);
			if (bufferPtr != null)
				ffmpeg.av_free(bufferPtr);
			if (streamHandle.IsAllocated)
				streamHandle.Free();
		}

		public static implicit operator StreamSource(Stream stream)
			=> new(stream);
	}
}