using Nox.FFmpeg.Base;

namespace Nox.FFmpeg.Utils {
	/// Simple data holder for a media stream.
	public sealed class MediaStream : IStream {
		public StreamType Type { get; }
		public string Url { get; }
		public bool IsOpen { get; private set; }

		public MediaStream(StreamType type, string url) {
			Type = type;
			Url  = url;
		}

		public void Open() 
            => IsOpen = true;
		public void Close() 
            => IsOpen = false;
	}
}