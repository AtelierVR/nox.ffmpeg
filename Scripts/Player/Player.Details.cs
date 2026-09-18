using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg {
	/// IVideoPlayerDetails implementation: everything the resolver exposes about the
	/// current media (title, subtitle, thumbnail…).
	public partial class Player {
		// ── Metadata (IVideoPlayerDetails implementation) ─────────────────
		/// <summary>The title of the current media, if known.</summary>
		public string Title { get; internal set; } = null;

		/// <summary>The subtitle of the current media, if known.</summary>
		public string Subtitle { get; internal set; } = null;

		/// <summary>The thumbnail of the current media, if known.</summary>
		public Texture2D Thumbnail { get; internal set; } = null;

		/// <summary>
		/// Event invoked when the metadata of the current media changes
		/// (title, subtitle, thumbnail…).
		/// </summary>
		public UnityEvent<IVideoPlayer> OnMetadata { get; } = new();

		/// <summary>
		/// Set the metadata of the current media, then notify <see cref="OnMetadata"/>.
		/// </summary>
		internal void SetMetadata(string title, string subtitle, Texture2D thumbnail = null) {
			Title     = title;
			Subtitle  = subtitle;
			Thumbnail = thumbnail;
			OnMetadata.Invoke(this);
		}

		// ── Delivery (IVideoPlayerDetails implementation) ──────────────────

		/// <summary>
		/// How the current media is delivered. A live stream reports no duration:
		/// FFmpeg gives <c>AV_NOPTS_VALUE</c> (a huge negative value) or nothing at all
		/// (NaN), so the player cannot be seeked.
		/// </summary>
		public PlayType Type {
			get {
				if (InternalState == null)
					return PlayType.None;
				var duration = Duration;
				return double.IsNaN(duration) || duration <= 0
					? PlayType.Stream
					: PlayType.Media;
			}
		}

		/// <inheritdoc />
		public bool IsStream
			=> Type == PlayType.Stream;
	}
}
