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
		private byte*          _dstBuffer;
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
			if (_dstBuffer != null) {
				ffmpeg.av_free(_dstBuffer);
				_dstBuffer = null;
			}
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

				// Remember the base pointer for freeing and for the returned frame.
				_dstBuffer = _dstData[0];

				// Force a tightly packed stride so consumers can copy
				// width*height*3 contiguous bytes.
				_dstLinesize[0] = _destinationSize.Width * 3;
				_dstLinesize[1] = 0;
				_dstLinesize[2] = 0;
				_dstLinesize[3] = 0;
				_dstAllocated = true;
			}

			// Vertical flip via FFmpeg: sws_scale supports negative destination
			// strides. Pointing at the last row and stepping backwards writes the
			// image bottom-to-top, matching the row order Texture2D expects.
			var stride   = _destinationSize.Width * 3;
			byte*[] dstData  = _dstData;
			int[]    dstLines = _dstLinesize;
			dstData[0]  = _dstBuffer + (_destinationSize.Height - 1) * stride;
			dstLines[0] = -stride;

			int ret = ffmpeg.sws_scale(_pConvertContext,
				sourceFrame.data,
				sourceFrame.linesize,
				0,
				sourceFrame.height,
				dstData,
				dstLines);
			if (ret < 0) {
				ret.ThrowExceptionIfError();
				throw new ApplicationException();
			}

			// _dstData/_dstLinesize still describe the base pointer with a positive
			// stride, so the returned frame points at the flipped, tightly-packed
			// buffer that can be copied contiguously.
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