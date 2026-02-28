using Nox.FFmpeg.Helpers;
using UnityEngine;

namespace Nox.FFmpeg {
	[RequireComponent(typeof(Player))]
	public class ExampleVideoPlayer : MonoBehaviour {
		public Player player;

		public bool autoPlay = true;
		public string contentUrl;

		private void Start() {
			player ??= GetComponent<Player>();
			Initializer.Initialize();

			if (autoPlay)
				Play();
		}

		[ContextMenu(nameof(Play))]
		public void Play() {
			if (string.IsNullOrEmpty(contentUrl))
				return;

			player.Play(contentUrl);
			Debug.Log("Done");
		}
	}
}