using FFmpeg.AutoGen;
namespace Nox.FFmpeg.Utils {
	// ─────────────────────────────────────────────────────────────────────────
	// Frame / FrameQueue — mirrors ffplay.c FrameQueue
	// ─────────────────────────────────────────────────────────────────────────
	public unsafe class Frame {
		public AVFrame* AVFrame;
		public int Serial;
		public double Pts;
		public double Duration;
		public long Pos;
		public int Width;
		public int Height;
		public int Format;

		public AVRational Sar;
		public bool Uploaded;

		public Frame()
			=> AVFrame = ffmpeg.av_frame_alloc();

		public void Unref()
			=> ffmpeg.av_frame_unref(AVFrame);

		public void Free() {
			var f = AVFrame;
			ffmpeg.av_frame_free(&f);
			AVFrame = null;
		}
	}
}