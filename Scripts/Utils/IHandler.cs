namespace Nox.FFmpeg.Utils {
	/// A typed processor that manages the streams of its <see cref="Type"/>.
	public interface IHandler {
		StreamType Type { get; }
		bool IsRunning { get; }
		void Start();
		void Stop();
	}
}
