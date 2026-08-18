using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Base;
using Nox.VideoPlayer;
using UnityEngine;

namespace Nox.FFmpeg.Utils {
	/// <summary>
	/// Non-<c>unsafe</c> helper that performs the asynchronous resolve step for
	/// <see cref="Player"/>. <see cref="Player"/> is an <c>unsafe</c> class, so
	/// <c>await</c> cannot be used directly inside it.
	/// </summary>
	internal static class PlayerResolver {
		public static async UniTask ResolveAndOpenAsync(Player player, IFetchOptions options) {
			try {
				var results  = await VideoPlayerResolver.Resolve(player, options);
				var resolves = results.SelectMany(e => e.Data).ToArray();
				if (resolves.Length == 0) {
					player.FireError("No data found for query");
					return;
				}

				var (video, audio) = resolves[0].FindQuality();
				if (video == null && audio == null) {
					player.FireError("No compatible stream found");
					return;
				}

				player.Title = resolves[0].Title;
				player.Subtitle = resolves[0].Subtitle;

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
					player.FireError("No compatible stream found");
					return;
				}

				player.Open(flux.ToArray());
			} catch (Exception e) {
				player.FireError(e.Message);
				Debug.LogException(e);
			}
		}
	}
}
