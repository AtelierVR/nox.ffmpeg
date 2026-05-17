namespace Nox.FFmpeg.Utils {

	// ─────────────────────────────────────────────────────────────────────────
	// Constants (mirror of ffplay.c defines)
	// ─────────────────────────────────────────────────────────────────────────
	public class Constants {
		public const int MAX_QUEUE_SIZE = 15 * 1024 * 1024;
		public const int MIN_FRAMES = 25;
		public const int EXTERNAL_CLOCK_MIN_FRAMES = 2;
		public const int EXTERNAL_CLOCK_MAX_FRAMES = 10;
		public const int AUDIO_MIN_BUFFER_SIZE = 512;
		public const int AUDIO_MAX_CALLBACKS_PER_SEC = 30;
		public const double AV_SYNC_THRESHOLD_MIN = 0.04;
		public const double AV_SYNC_THRESHOLD_MAX = 0.1;
		public const double AV_SYNC_FRAMEDUP_THRESHOLD = 0.1;
		public const double AV_NOSYNC_THRESHOLD = 10.0;
		public const int SAMPLE_CORRECTION_MAX = 10;
		public const double EXTERNAL_CLOCK_SPEED_MIN = 0.900;
		public const double EXTERNAL_CLOCK_SPEED_MAX = 1.010;
		public const double EXTERNAL_CLOCK_SPEED_STEP = 0.001;
		public const int AUDIO_DIFF_AVG_NB = 20;
		public const double REFRESH_RATE = 0.01;
		public const int VIDEO_PICTURE_QUEUE_SIZE = 3;
		public const int SAMPLE_QUEUE_SIZE = 9;
		public const int FRAME_QUEUE_SIZE = 16; // FFMAX of all queues

		public const int AV_SYNC_AUDIO_MASTER = 0;
		public const int AV_SYNC_VIDEO_MASTER = 1;
		public const int AV_SYNC_EXTERNAL_CLOCK = 2;
	}
}