using System;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;

namespace Nox.FFmpeg.Sources {
	public sealed unsafe class UrlSource : BaseSource {
		private readonly string _url;

		public UrlSource(string url) {
			if (string.IsNullOrWhiteSpace(url))
				return;

			Debug.Log($"Opening url {url}");
			_url                                  =  url;
			_pFormatContext                       =  ffmpeg.avformat_alloc_context();
			_pFormatContext->flags                |= ffmpeg.AVFMT_FLAG_SHORTEST;
			_pFormatContext->max_interleave_delta =  100_000_000;
			_pFormatContext->avio_flags           =  ffmpeg.AVIO_FLAG_READ | ffmpeg.AVIO_FLAG_NONBLOCK;

			var pFormatContext = _pFormatContext;
			ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
			ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();

			_videoIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);

			_pPacket = ffmpeg.av_packet_alloc();
			IsValid  = true;
		}

		public static implicit operator UrlSource(string url)
			=> new(url);
	}
}