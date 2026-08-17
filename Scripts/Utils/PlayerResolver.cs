using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.VideoPlayer;
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

				if (video != null)
					player.Open(video.Url, audio?.Url);
				else
					player.Open(audio.Url);
			} catch (Exception e) {
				player.FireError(e.Message);
				Debug.LogException(e);
			}
		}
	}
}
