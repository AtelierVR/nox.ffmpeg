namespace Nox.FFmpeg.Utils {
	/// A media stream (flux) provided by a source URL.
	public interface IStream {
		StreamType Type { get; }
		string Url { get; }
		bool IsOpen { get; }
		void Open();
		void Close();
	}

	/// Simple data holder for a media stream.
	public sealed class MediaStream : IStream {
		public StreamType Type { get; }
		public string Url { get; }
		public bool IsOpen { get; private set; }

		public MediaStream(StreamType type, string url) {
			Type = type;
			Url  = url;
		}

		public void Open()  => IsOpen = true;
		public void Close() => IsOpen = false;
	}
}
