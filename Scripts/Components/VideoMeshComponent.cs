using System;
using UnityEngine;
using Nox.FFmpeg.Handlers;

namespace Nox.FFmpeg.Components {

	/// How the video texture is mapped onto the mesh.
	public enum VideoMeshFit {
		/// Fill the mesh entirely, preserving aspect ratio and cropping the overflow.
		Cover,
		/// Stretch the video to occupy the whole mesh (distorts the aspect ratio).
		Fill,
		/// Show the whole video inside the mesh without distortion (letterbox/pillarbox).
		Contain
	}

	/// One entry: a Renderer + which material slot + which shader property to drive.
	[Serializable]
	public class VideoMeshTarget {
		[Tooltip("Renderer to update. Leave null to use the Renderer on this GameObject.")]
		public Renderer Renderer;

		[Tooltip("Material index inside the Renderer.")]
		public int MaterialIndex;

		[Tooltip("Shader texture property name (e.g. _MainTex, _EmissionMap).")]
		public string TextureProperty = "_MainTex";

		[Tooltip("How the video is fitted onto the mesh: Cover crops, Fill stretches, Contain letterboxes.")]
		public VideoMeshFit Fit = VideoMeshFit.Fill;

		[Tooltip("Flip the video vertically. VideoHandler already outputs upright frames, so this is off by default.")]
		public bool FlipVertical = false;

		[Tooltip("Flip the video horizontally (mirror left/right).")]
		public bool FlipHorizontal = false;

		[Tooltip("When letterboxing (Contain), fill the empty bars with transparent pixels instead of black. Requires a transparent shader.")]
		public bool TransparentBars = true;

		[Tooltip("If true, uses sharedMaterial instead of a per-instance material (avoids allocation but affects all instances).")]
		public bool UseSharedMaterial;

		[NonSerialized] internal Texture2D _containCache;
		[NonSerialized] internal int _cacheWidth;
		[NonSerialized] internal int _cacheHeight;
		[NonSerialized] internal Color32[] _compositePixels;

		internal void ReleaseCache() {
			if (_containCache) {
				UnityEngine.Object.Destroy(_containCache);
				_containCache = null;
			}
			_cacheWidth      = 0;
			_cacheHeight     = 0;
			_compositePixels = null;
		}
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

		// ── Utils ───────────────────────────────────────────────────────── 

		/// Current video texture from the Player (null if none).
		public VideoHandler Video 
			=> Source.State?.GetHandler<VideoHandler>();

		// ── Lifecycle ──────────────────────────────────────────────────────

		private void Awake() {
			Source ??= GetComponentInParent<Player>(includeInactive: true);
		}

		private void OnEnable() {
			if(Source.State != null)
				HandleStateChanged(Source.State);
			Source.OnStateChanged.AddListener(HandleStateChanged);
		}

		private void HandleStateChanged(PlayerState state) {
			if (Video == null) return;
			Video.OnTexture.AddListener(HandleFrame);
            HandleFrame(Video.Frame);
		}

		private void OnDisable() {
			Video?.OnTexture.RemoveListener(HandleFrame);
			Source.OnStateChanged.RemoveListener(HandleStateChanged);
		}

		private void OnDestroy() {
			if (Targets == null)
				return;
			foreach (var t in Targets)
				t?.ReleaseCache();
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

			Material mat;
			if (target.UseSharedMaterial) {
				var mats = target.Renderer.sharedMaterials;
				if (target.MaterialIndex < 0 || target.MaterialIndex >= mats.Length) return;
				mat = mats[target.MaterialIndex];
			} else {
				var mats = target.Renderer.materials;
				if (target.MaterialIndex < 0 || target.MaterialIndex >= mats.Length) return;
				mat = mats[target.MaterialIndex];
				target.Renderer.materials = mats;
			}

			if (!mat) return;
			if (frame.wrapMode != TextureWrapMode.Clamp)
				frame.wrapMode = TextureWrapMode.Clamp;
			mat.SetTexture(target.TextureProperty, frame);
			ApplyFit(target, mat, frame);
		}

		private static void ApplyFit(VideoMeshTarget target, Material mat, Texture2D frame) {
			if (frame == null)
				return;

			var meshAspect  = GetMeshAspect(target.Renderer);
			var videoAspect = (float)frame.width / Mathf.Max(1, frame.height);

			// Fill or unknown/degenerate aspect → plain stretch.
			if (target.Fit == VideoMeshFit.Fill || meshAspect <= 0f || videoAspect <= 0f) {
				SetFitTransform(mat, target, Vector2.one, Vector2.zero);
				return;
			}

			meshAspect = Mathf.Clamp(meshAspect, 0.001f, 1000f);

			Vector2 scale, offset;
			if (target.Fit == VideoMeshFit.Contain) {
				if (videoAspect > meshAspect) { // letterbox top/bottom
					scale  = new Vector2(1f, videoAspect / meshAspect);
					offset = new Vector2(0f, (1f - scale.y) * 0.5f);
				} else {                          // pillarbox left/right
					scale  = new Vector2(meshAspect / videoAspect, 1f);
					offset = new Vector2((1f - scale.x) * 0.5f, 0f);
				}
			} else { // Cover
				if (videoAspect > meshAspect) {   // crop left/right
					scale  = new Vector2(meshAspect / videoAspect, 1f);
					offset = new Vector2((1f - scale.x) * 0.5f, 0f);
				} else {                          // crop top/bottom
					scale  = new Vector2(1f, videoAspect / meshAspect);
					offset = new Vector2(0f, (1f - scale.y) * 0.5f);
				}
			}

			// Contain with transparent bars needs a composite texture: scale/offset
			// alone cannot produce alpha in the empty areas.
			if (target.Fit == VideoMeshFit.Contain && target.TransparentBars
				&& Mathf.Abs(meshAspect - videoAspect) > 0.001f) {
				var tex = GetContainedTexture(target, frame, meshAspect, videoAspect);
				if (tex != null) {
					mat.SetTexture(target.TextureProperty, tex);
					SetFitTransform(mat, target, Vector2.one, Vector2.zero, false);
					return;
				}
			}

			SetFitTransform(mat, target, scale, offset);
		}

		private static void SetFitTransform(Material mat, VideoMeshTarget target, Vector2 scale, Vector2 offset, bool applyFlips = true) {
			if (applyFlips) {
				if (target.FlipVertical) {
					var sy = scale.y;
					var oy = offset.y;
					scale.y  = -sy;
					offset.y = sy + oy;
				}
				if (target.FlipHorizontal) {
					var sx = scale.x;
					var ox = offset.x;
					scale.x  = -sx;
					offset.x = sx + ox;
				}
			}
			mat.SetTextureScale(target.TextureProperty, scale);
			mat.SetTextureOffset(target.TextureProperty, offset);
		}

		private static Texture2D GetContainedTexture(VideoMeshTarget target, Texture2D frame, float meshAspect, float videoAspect) {
			const int MaxTex = 8192;

			long texW, texH;
			if (videoAspect > meshAspect) {
				texW = frame.width;
				texH = Mathf.RoundToInt(frame.width / meshAspect);
			} else {
				texH = frame.height;
				texW = Mathf.RoundToInt(frame.height * meshAspect);
			}

			if (texW < frame.width || texH < frame.height || texW <= 0 || texH <= 0 || texW > MaxTex || texH > MaxTex)
				return null;

			int w = (int)texW, h = (int)texH;

			if (target._containCache == null || target._cacheWidth != w || target._cacheHeight != h) {
				target.ReleaseCache();
				target._containCache = new Texture2D(w, h, TextureFormat.RGBA32, false) {
					name     = "FFplayContain",
					wrapMode = TextureWrapMode.Clamp
				};
				target._cacheWidth  = w;
				target._cacheHeight = h;
			}

			var tex  = target._containCache;
			var dstX = (w - frame.width) / 2;
			var dstY = (h - frame.height) / 2;

			// Reuse the destination buffer across frames to avoid per-frame allocation.
			if (target._compositePixels == null || target._compositePixels.Length != w * h)
				target._compositePixels = new Color32[w * h];

			var pixels = target._compositePixels;
			var src    = frame.GetPixels32();
			Array.Clear(pixels, 0, pixels.Length); // transparent background

			if (!target.FlipHorizontal) {
				for (int y = 0; y < frame.height; y++) {
					int srcY = target.FlipVertical ? frame.height - 1 - y : y;
					Array.Copy(src, srcY * frame.width, pixels, (dstY + y) * w + dstX, frame.width);
				}
			} else {
				for (int y = 0; y < frame.height; y++) {
					int srcY = target.FlipVertical ? frame.height - 1 - y : y;
					int row  = (dstY + y) * w + dstX;
					for (int x = 0; x < frame.width; x++)
						pixels[row + x] = src[srcY * frame.width + (frame.width - 1 - x)];
				}
			}

			tex.SetPixels32(pixels);
			tex.Apply(false);
			return tex;
		}

		private static float GetMeshAspect(Renderer renderer) {
			// Account for the mesh's local shape AND its transform scale (e.g. a
			// 10x10 plane scaled to 16:9). UV.u maps to local X, so X is the
			// horizontal axis; the vertical axis is the larger of Y/Z (a Unity
			// Plane lies in XZ, a Quad in XY).
			var mesh = (renderer as MeshRenderer)?.GetComponent<MeshFilter>()?.sharedMesh
				?? (renderer as SkinnedMeshRenderer)?.sharedMesh;
			if (mesh != null) {
				var size  = mesh.bounds.size;
				var scale = renderer.transform.lossyScale;
				var w = size.x * Mathf.Abs(scale.x);
				var h = Mathf.Max(size.y * Mathf.Abs(scale.y), size.z * Mathf.Abs(scale.z));
				if (w > 0f && h > 0f)
					return w / h;
			}

			// Fallback: aspect of the two largest world-bounds axes.
			var b    = renderer.bounds.size;
			var dims = new[] { b.x, b.y, b.z };
			Array.Sort(dims);
			return dims[1] > 0f ? dims[2] / dims[1] : 0f;
		}
	}
}
