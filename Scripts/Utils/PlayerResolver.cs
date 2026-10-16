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

				await player.OpenResolveAsync(resolves[0]);
			} catch (Exception e) {
				player.Log(LogType.Exception, e.Message);
				Debug.LogException(e);
			}
		}

		/// <summary>
		/// Open an already resolved media at the best compatible quality: refresh the
		/// metadata (title/subtitle/thumbnail) then open the matching flux. Used both
		/// after a resolve and when the user picks a result in the search page.
		/// </summary>
		public static async UniTask OpenResolveAsync(this Player player, IResolve resolve) {
			try {
				if (resolve == null) {
					player.Log(LogType.Error, "No media to open");
					return;
				}

				var (video, audio) = resolve.FindQuality();
				if (video == null && audio == null) {
					// A "flat" search entry has no format: re-resolve its page url through the
					// handlers so the full extraction happens (the guard stops a re-entrance if
					// that url resolves to the very same, format-less, entry).
					if (!string.IsNullOrEmpty(resolve.Url) && !string.Equals(resolve.Url, player.Url, StringComparison.Ordinal)) {
						player.Log(LogType.Log, $"Resolving {resolve.Url}");
						player.Play(resolve.Url);
						return;
					}

					player.Log(LogType.Error, "No compatible stream found");
					return;
				}

				player.SetMetadata(
					resolve.Title,
					resolve.Subtitle,
					await FetchThumbnailAsync(resolve.Thumbnails)
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
