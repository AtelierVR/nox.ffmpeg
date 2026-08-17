using System;

namespace Nox.FFmpeg.Base {
	/// The kind of a media stream / handler.
	[Flags]
	public enum StreamType {
		Video    = 1 << 0,
		Audio    = 1 << 1,
		Subtitle = 1 << 2,
		Av       = Video | Audio,
	}
}
