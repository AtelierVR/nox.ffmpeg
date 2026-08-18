using System.Collections.Generic;

namespace Nox.FFmpeg.Base {
	/// A media stream (flux) provided by a source URL.
	public interface IStream {
		StreamType Type { get; }

		string Url { get; }
		Dictionary<string, string> Headers { get; }
		
		bool IsOpen { get; }
		void Open();
		void Close();
	}
}
