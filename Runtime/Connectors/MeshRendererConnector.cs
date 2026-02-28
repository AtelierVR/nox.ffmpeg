using Nox.FFmpeg.Workers;
using UnityEngine;

namespace Nox.FFmpeg.Connectors {
	[RequireComponent(typeof(MeshRenderer))]
	public class MeshRendererConnector : MonoBehaviour {
		public VideoWorker worker;
		private MeshRenderer renderMesh;
		public int materialIndex = -1;
		public string[] textureProperties = {
			"_EmissionMap",
			"_BaseMap",
			"_MainTex"
		};

		private MaterialPropertyBlock propertyBlock;
		public bool updateGIRealtime = true;

		private void Start() {
			worker ??= GetComponentInParent<VideoWorker>(true);
			if (!worker)
				Debug.LogWarning($"No {nameof(VideoWorker)} found.");

			propertyBlock = new MaterialPropertyBlock();
			renderMesh    = GetComponent<MeshRenderer>();
			worker.OnDisplay.AddListener(Display);
		}

		private void OnDestroy()
			=> worker.OnDisplay.RemoveListener(Display);

		public void Display(Texture2D texture) {
			if (texture)
				foreach (var t in textureProperties)
					propertyBlock.SetTexture(t, texture);
			
			if (!renderMesh)
				return;

			if (materialIndex == -1)
				renderMesh.SetPropertyBlock(propertyBlock);
			else
				renderMesh.SetPropertyBlock(propertyBlock, materialIndex);

			if (updateGIRealtime)
				renderMesh.UpdateGIMaterials();
		}
	}
}