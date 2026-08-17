using System;
using System.Drawing;
using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Helpers {
	public sealed unsafe class Converter : IDisposable {
		private readonly Size _destinationSize;
		private readonly AVPixelFormat _destinationPixelFormat;
		private readonly SwsContext* _pConvertContext;

		private byte_ptrArray4 _dstData;
		private int_array4     _dstLinesize;
		private bool           _dstAllocated;

		public Converter(
			Size sourceSize,      AVPixelFormat sourcePixelFormat,
			Size destinationSize, AVPixelFormat destinationPixelFormat
		) {
			_destinationSize        = destinationSize;
			_destinationPixelFormat = destinationPixelFormat;

			_pConvertContext = ffmpeg.sws_getContext(sourceSize.Width,
				sourceSize.Height,
				sourcePixelFormat,
				destinationSize.Width,
				destinationSize.Height,
				destinationPixelFormat,
				ffmpeg.SWS_POINT,
				// ffmpeg.SWS_FAST_BILINEAR,
				null,
				null,
				null);
			if (_pConvertContext == null)
				throw new ApplicationException("Could not initialize the conversion context.");
		}

		public void Dispose() {
			FreeBuffer();
			ffmpeg.sws_freeContext(_pConvertContext);
		}

		private void FreeBuffer() {
			if (!_dstAllocated)
				return;
			fixed (void* p = &_dstData.ToArray()[0])
				ffmpeg.av_freep(p);
			_dstAllocated = false;
		}

		/// Converts the source frame into a reusable RGB24 destination buffer.
		/// The buffer is allocated once and reused, avoiding per-frame allocation.
		public AVFrame Convert(AVFrame sourceFrame) {
			if (!_dstAllocated) {
				_dstData     = new byte_ptrArray4();
				_dstLinesize = new int_array4();
				ffmpeg.av_image_alloc(ref _dstData, ref _dstLinesize,
					_destinationSize.Width, _destinationSize.Height,
					_destinationPixelFormat, 32).ThrowExceptionIfError();
				// Force a tightly packed stride so consumers can copy
				// width*height*3 contiguous bytes.
				_dstLinesize[0] = _destinationSize.Width * 3;
				_dstLinesize[1] = 0;
				_dstLinesize[2] = 0;
				_dstLinesize[3] = 0;
				_dstAllocated = true;
			}

			int ret = ffmpeg.sws_scale(_pConvertContext,
				sourceFrame.data,
				sourceFrame.linesize,
				0,
				sourceFrame.height,
				_dstData,
				_dstLinesize);
			if (ret < 0) {
				ret.ThrowExceptionIfError();
				throw new ApplicationException();
			}

			var data = new byte_ptrArray8();
			data.UpdateFrom(_dstData);
			var linesize = new int_array8();
			linesize.UpdateFrom(_dstLinesize);

			return new AVFrame {
				data     = data,
				linesize = linesize,
				width    = _destinationSize.Width,
				height   = _destinationSize.Height,
			};
		}
	}
}