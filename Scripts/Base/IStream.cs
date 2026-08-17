namespace Nox.FFmpeg.Base {
	/// A media stream (flux) provided by a source URL.
	public interface IStream {
		StreamType Type { get; }
		string Url { get; }
		bool IsOpen { get; }
		void Open();
		void Close();
	}
}
