using Nox.VideoPlayer;

namespace Nox.FFmpeg {
	public partial class Player {
		/// <summary>
		/// Video streams of the current media, and the one being decoded by the video
		/// handler. Null while no media is opened.
		/// </summary>
		public ITrack VideoTracks
			=> InternalState?.Video?.Track;

		/// <summary>
		/// Audio streams of the current media, and the one being decoded by the audio
		/// handler. Null while no media is opened.
		/// </summary>
		public ITrack AudioTracks
			=> InternalState?.Audio?.Track;
	}
}
