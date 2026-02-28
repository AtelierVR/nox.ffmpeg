using System;
using System.Diagnostics;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;

namespace Nox.FFmpeg.Sources {
	public unsafe abstract class BaseSource : IDisposable {
		internal AVFormatContext* _pFormatContext;
		internal AVPacket* _pPacket;
		internal int _videoIndex = 0;

		public bool IsEnded { get; protected set; } = false;
		public bool IsValid { get; protected set; } = false;

		// Buffering/Stalled detection (thread-safe)
		private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
		private int _consecutiveEAgainCount = 0;
		private double _lastSuccessfulReadTime = 0;
		private bool _isBuffering = false;
		private bool _isStalled = false;
		private const int BUFFERING_THRESHOLD = 10; // EAGAIN count before buffering
		private const double STALLED_TIMEOUT = 5.0; // seconds without data before stalled

		public bool IsBuffering => _isBuffering;
		public bool IsStalled => _isStalled;

		public event Action OnBufferingStart;
		public event Action OnBufferingEnd;
		public event Action OnStalledStart;
		public event Action OnStalledEnd;
		
		private double GetCurrentTime() => _stopwatch.Elapsed.TotalSeconds;

		public bool HasStream(AVMediaType type) {
			if (!IsValid)
				return false;
			return ffmpeg.av_find_best_stream(_pFormatContext, type, -1, -1, null, 0) >= 0;
		}

		public double GetLength(StreamDecoder decoder) {
			int _streamIndex = decoder._streamIndex;
			if (_streamIndex < 0 || _streamIndex >= _pFormatContext->nb_streams)
				return 0d;
			AVRational base_q = _pFormatContext->streams[_streamIndex]->time_base;
			long       offset = _pFormatContext->streams[_streamIndex]->duration;
			double     time   = ffmpeg.av_q2d(base_q);
			return offset * time;
		}

		public bool TryGetFps(out double fps) {
			fps = default;
			int _streamIndex = _pPacket->stream_index;
			_streamIndex = _videoIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			fps = (double)_pFormatContext->streams[_streamIndex]->avg_frame_rate.num / _pFormatContext->streams[_streamIndex]->avg_frame_rate.den;
			return true;
		}

		public bool TryGetFps(StreamDecoder decoder, out double fps) {
			fps = default;
			int _streamIndex = decoder._streamIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			fps = (double)_pFormatContext->streams[_streamIndex]->avg_frame_rate.num / _pFormatContext->streams[_streamIndex]->avg_frame_rate.den;
			return true;
		}

		public bool TryGetPts(out double fps) {
			fps = default;
			int _streamIndex = _pPacket->stream_index;
			_streamIndex = _videoIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			fps = (double)_pFormatContext->streams[_streamIndex]->time_base.den / ((double)_pFormatContext->streams[_streamIndex]->avg_frame_rate.num / _pFormatContext->streams[_streamIndex]->avg_frame_rate.den);
			return true;
		}

		public bool TryGetPts(StreamDecoder decoder, out double fps) {
			fps = default;
			int _streamIndex = decoder._streamIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			fps = (double)_pFormatContext->streams[_streamIndex]->time_base.den / ((double)_pFormatContext->streams[_streamIndex]->avg_frame_rate.num / _pFormatContext->streams[_streamIndex]->avg_frame_rate.den);
			return true;
		}

		public bool TryGetTimeBase(AVMediaType type, out AVRational timebase) {
			timebase = default;
			var _streamIndex = ffmpeg.av_find_best_stream(_pFormatContext, type, -1, -1, null, 0);
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			timebase = _pFormatContext->streams[_streamIndex]->time_base;
			return true;
		}

		public bool TryGetStart(out double start) {
			start = default;
			int _streamIndex = _pPacket->stream_index;
			_streamIndex = _videoIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			double timebase = (double)_pFormatContext->streams[_streamIndex]->time_base.num / _pFormatContext->streams[_streamIndex]->time_base.den;
			start = _pFormatContext->streams[_streamIndex]->start_time * timebase;
			return start != ffmpeg.AV_NOPTS_VALUE;
		}

		public bool TryGetTime(out double time) {
			time = default;
			if (_pPacket == null)
				return false;
			int _streamIndex = _pPacket->stream_index;
			_streamIndex = _videoIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			double timebase = (double)_pFormatContext->streams[_streamIndex]->time_base.num / _pFormatContext->streams[_streamIndex]->time_base.den;
			time = _pPacket->pts * timebase;
			return true;
		}

		public bool TryGetTime(StreamDecoder decoder, out double time) {
			time = default;
			int _streamIndex = decoder._streamIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			double timebase = (double)_pFormatContext->streams[_streamIndex]->time_base.num / _pFormatContext->streams[_streamIndex]->time_base.den;
			time = _pPacket->pts * timebase;
			return true;
		}

		public bool TryGetTime(StreamDecoder decoder, AVFrame frame, out double time) {
			time = default;
			int _streamIndex = decoder._streamIndex;
			if (_streamIndex < 0 || _streamIndex > _pFormatContext->nb_streams)
				return false;
			double timebase = (double)_pFormatContext->streams[_streamIndex]->time_base.num / _pFormatContext->streams[_streamIndex]->time_base.den;
			time = frame.pts * timebase;
			return true;
		}

		public bool NextFrame(out AVPacket packet) {
			if (!IsValid) {
				packet = default;
				return false;
			}
			int error;
			int eagainCount = 0;
			do {
				ffmpeg.av_packet_unref(_pPacket);
				error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
				if (error == ffmpeg.AVERROR_EOF) {
					IsEnded = true;
					packet  = default;
					ResetBufferingState();
					return false;
				}
				
				if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
					eagainCount++;
					_consecutiveEAgainCount++;
					
					// Check for buffering
					if (_consecutiveEAgainCount >= BUFFERING_THRESHOLD && !_isBuffering) {
						_isBuffering = true;
						OnBufferingStart?.Invoke();
					}
					
					// Check for stalled
					double currentTime = GetCurrentTime();
					if (_lastSuccessfulReadTime > 0 && (currentTime - _lastSuccessfulReadTime) > STALLED_TIMEOUT) {
						if (!_isStalled) {
							_isStalled = true;
							OnStalledStart?.Invoke();
						}
					}
					continue;
				}
				
				error.ThrowExceptionIfError();
			} while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) && eagainCount < 100);
			
			if (error == 0) {
				// Successful read
				_lastSuccessfulReadTime = GetCurrentTime();
				_consecutiveEAgainCount = 0;
				
				// End buffering if it was active
				if (_isBuffering) {
					_isBuffering = false;
					OnBufferingEnd?.Invoke();
				}
				
				// End stalled if it was active
				if (_isStalled) {
					_isStalled = false;
					OnStalledEnd?.Invoke();
				}
				
				packet  = *_pPacket;
				IsEnded = false;
				return true;
			}
			
			packet = default;
			return false;
		}
		
		private void ResetBufferingState() {
			if (_isBuffering) {
				_isBuffering = false;
				OnBufferingEnd?.Invoke();
			}
			if (_isStalled) {
				_isStalled = false;
				OnStalledEnd?.Invoke();
			}
			_consecutiveEAgainCount = 0;
		}

		public void Seek(int index, long offset) {
			if (!IsValid)
				return;
			int flags = ffmpeg.AVSEEK_FLAG_BACKWARD;
			ffmpeg.av_seek_frame(_pFormatContext, index, offset, flags).ThrowExceptionIfError();
		}

		public void Seek(StreamDecoder decoder, double offset) {
			if (!IsValid)
				return;
			int        _streamIndex = decoder._streamIndex;
			AVRational base_q       = ffmpeg.av_get_time_base_q();
			base_q = _pFormatContext->streams[_streamIndex]->time_base;
			double pts   = (double)base_q.num / base_q.den;
			long   frame = ffmpeg.av_rescale((long)(offset * 1000d), base_q.den, base_q.num);
			frame /= 1000;
			Seek(_streamIndex, Math.Max(0, frame));
		}

		public virtual void Dispose() {
			if (!IsValid)
				return;
			IsValid = false;

			var pPacket = _pPacket;
			ffmpeg.av_packet_free(&pPacket);

			var pFormatContext = _pFormatContext;
			ffmpeg.avformat_close_input(&pFormatContext);
		}

		public AVMediaType GetMediaType(AVPacket packet) {
			if (!IsValid || packet.stream_index < 0 || packet.stream_index >= _pFormatContext->nb_streams)
				return AVMediaType.AVMEDIA_TYPE_UNKNOWN;
			return _pFormatContext->streams[packet.stream_index]->codecpar->codec_type;
		}
	}
}