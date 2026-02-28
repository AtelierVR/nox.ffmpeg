using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Helpers {
	static internal class Helper {
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
	}
}