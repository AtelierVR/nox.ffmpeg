using Nox.FFmpeg.Handlers;
using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg {
	public partial class Player {
		// ── Frame / Texture ───────────────────────────────────────────────
		public Texture2D Texture 
            => InternalState?.GetHandler<VideoHandler>()?.Frame;
            
		public UnityEvent<IVideoPlayer, Texture2D> OnTexture { get; } = new();

		// ── Resolution ────────────────────────────────────────────────────
		public Vector2Int Resolution 
            => InternalState?.GetHandler<VideoHandler>()?.Resolution 
                ?? Vector2Int.zero;

		public UnityEvent<IVideoPlayer, Vector2Int> OnResolution { get; } = new();
	}
}