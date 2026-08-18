using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Base {
	/// Base class for typed processors that manage the streams of their <see cref="Type"/>.
	public abstract unsafe class IHandler {
		public abstract StreamType Type { get; }

		/// True once <see cref="Start"/> has run and <see cref="Stop"/> hasn't.
		public bool IsRunning { get; protected set; }

		/// FFmpeg stream index selected by the demuxer (-1 until opened).
		public int StreamIndex { get; set; } = -1;
		public AVStream* StreamPtr { get; set; }

		/// True when the stream has been decoded to the end and drained.
		public abstract bool HasEnded { get; }

		/// True when the demux queue for this stream has enough packets (not starving).
		public abstract bool HasEnoughPackets { get; }

		public abstract void Start();
		public abstract void Stop();
	}
}
