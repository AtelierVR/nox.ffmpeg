using System;
using UnityEngine;

namespace Nox.FFmpeg.Utils {

	/// One entry: a Renderer + which material slot + which shader property to drive.
	[Serializable]
	public class VideoMeshTarget {
		[Tooltip("Renderer to update. Leave null to use the Renderer on this GameObject.")]
		public Renderer Renderer;

		[Tooltip("Material index inside the Renderer.")]
		public int MaterialIndex;

		[Tooltip("Shader texture property name (e.g. _MainTex, _EmissionMap).")]
		public string TextureProperty = "_MainTex";

		[Tooltip("If true, uses sharedMaterial instead of a per-instance material (avoids allocation but affects all instances).")]
		public bool UseSharedMaterial;
	}

	/// Subscribes to a Player's OnVideoFrame event and applies the
	/// resulting Texture2D to one or more Renderer / material-slot / shader-property combinations.
	[AddComponentMenu("Nox/FFmpeg/Video Mesh")]
	public class VideoMeshComponent : MonoBehaviour {

		[Header("Source")]
		[Tooltip("Player to subscribe to. Leave null to search on the same or parent GameObject.")]
		public Player Source;

		[Header("Targets")]
		public VideoMeshTarget[] Targets = Array.Empty<VideoMeshTarget>();

		// ── Lifecycle ──────────────────────────────────────────────────────

		private void Awake() {
			Source ??= GetComponentInParent<Player>(includeInactive: true);
		}

		private void OnEnable() {
			if (!Source) return;
			Source.OnFrame.AddListener(HandleFrame);
            HandleFrame(Source.Frame); // Apply current frame immediately on enable
		}

		private void OnDisable() {
			Source?.OnFrame.RemoveListener(HandleFrame);
		}

		// ── Frame handler ──────────────────────────────────────────────────

		private void HandleFrame(Texture2D frame) {
			if (frame == null)
				return;
			foreach (var t in Targets)
				Apply(t, frame);
		}

		private static void Apply(VideoMeshTarget target, Texture2D frame) {
			if (!target.Renderer) return;

			if (target.UseSharedMaterial) {
				var mats = target.Renderer.sharedMaterials;
				if (target.MaterialIndex < 0 || target.MaterialIndex >= mats.Length) return;
				mats[target.MaterialIndex].SetTexture(target.TextureProperty, frame);
			} else {
				var mats = target.Renderer.materials;
				if (target.MaterialIndex < 0 || target.MaterialIndex >= mats.Length) return;
				mats[target.MaterialIndex].SetTexture(target.TextureProperty, frame);
				target.Renderer.materials = mats;
			}
		}
	}
}
