using Cysharp.Threading.Tasks;
using Nox.FFmpeg.Utils;
using Nox.VideoPlayer;
using UnityEngine;

namespace Nox.FFmpeg {
	/// <summary>
	/// Permission + source surface of the player (<see cref="IVideoPlayerControl"/>,
	/// <see cref="IVideoPlayerSource"/>): whether the local user may change the media and
	/// how to open an already resolved media.
	/// </summary>
	public partial class Player {
		[Header("Permissions")]
		[Tooltip("When true, the local user cannot change the video: no new url, no search.")]
		[SerializeField]
		private bool _readonly;

		/// <inheritdoc />
		public bool IsReadonly {
			get => _readonly;
			set => _readonly = value;
		}

		/// <inheritdoc />
		public void Play(IResolve resolve)
			=> PlayerResolver.OpenResolveAsync(this, resolve).Forget();
	}
}
