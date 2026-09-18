using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Base;
using Nox.VideoPlayer;
using UnityEngine;
using LogType = Nox.CCK.Utils.LogType;

namespace Nox.FFmpeg.Utils {
	/// <summary>
	/// Non-<c>unsafe</c> helper that performs the asynchronous resolve step for
	/// <see cref="Player"/>. <see cref="Player"/> is an <c>unsafe</c> class, so
	/// <c>await</c> cannot be used directly inside it.
	/// </summary>
	internal static class PlayerResolver {
		public static async UniTask ResolveAndOpenAsync(this Player player, IFetchOptions options) {
			try {
				var results  = await VideoPlayerResolver.Resolve(player, options);
				var resolves = results.SelectMany(e => e.Data).ToArray();
				if (resolves.Length == 0) {
					player.Log(LogType.Error, "No data found for query");
					return;
				}

				var (video, audio) = resolves[0].FindQuality();
				if (video == null && audio == null) {
					player.Log(LogType.Error, "No compatible stream found");
					return;
				}

				player.SetMetadata(
					resolves[0].Title,
					resolves[0].Subtitle,
					await FetchThumbnailAsync(resolves[0].Thumbnails)
				);

				var flux = new List<Flux>();

				if (video != null && !string.IsNullOrEmpty(video.Url)) {
					var type = video is IAudio && video is IVideo ? StreamType.Av
						: video is IVideo ? StreamType.Video
						: StreamType.Audio;
					flux.Add(new Flux(type, video.Url, video.Headers));
				}

				if (audio != null && !string.IsNullOrEmpty(audio.Url)
					&& !string.Equals(audio.Url, video?.Url, StringComparison.Ordinal))
					flux.Add(new Flux(StreamType.Audio, audio.Url, audio.Headers));

				if (flux.Count == 0) {
					player.Log(LogType.Error, "No compatible stream found");
					return;
				}

				player.Open(flux.ToArray());
			} catch (Exception e) {
				player.Log(LogType.Exception, e.Message);
				Debug.LogException(e);
			}
		}

		/// <summary>
		/// Fetch the largest thumbnail offered by the resolver, or <c>null</c> when the
		/// source has none.
		/// </summary>
		private static async UniTask<Texture2D> FetchThumbnailAsync(IThumbnail[] thumbnails) {
			if (thumbnails == null || thumbnails.Length == 0)
				return null;

			var best = thumbnails
				.Where(t => t != null)
				.OrderByDescending(t => (long)t.Resolution.x * t.Resolution.y)
				.FirstOrDefault();
			if (best == null)
				return null;

			try {
				return await best.Fetch();
			} catch (Exception e) {
				Debug.LogException(e);
				return null;
			}
		}
	}
}
