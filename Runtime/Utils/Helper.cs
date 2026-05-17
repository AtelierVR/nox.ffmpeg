using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Utils {
	public static class Helper {
		
		// ─────────────────────────────────────────────────────────────────────────
		// Helper
		// ─────────────────────────────────────────────────────────────────────────
		public static unsafe string AvErr(int err) {
			const int sz  = 256;
			var       buf = stackalloc byte[ sz ];
			ffmpeg.av_strerror(err, buf, sz);
			return Marshal.PtrToStringAnsi((IntPtr)buf) ?? err.ToString();
		}
	}
}