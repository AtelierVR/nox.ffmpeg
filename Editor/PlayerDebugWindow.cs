using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;
using Nox.FFmpeg;
using Nox.FFmpeg.Connectors;
using Nox.FFmpeg.Workers;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nox.FFmpeg.Editor {
	public class PlayerDebugWindow : EditorWindow {
		private Player[] _players = Array.Empty<Player>();
		private int _selectedIndex = 0;
		private double _lastRefresh = 0;
		private const double RefreshInterval = 0.1;

		private Vector2 _scrollPos;
		private bool _foldWorkers = true;
		private bool _foldClocks = true;
		private bool _foldConnectors = true;
		private bool _foldPlayer = true;
		private readonly Dictionary<string, bool> _workerFolds = new();

		[MenuItem("Nox/FFmpeg/Player Debug")]
		public static void OpenWindow() {
			var window = GetWindow<PlayerDebugWindow>("FFmpeg Player Debug");
			window.minSize = new Vector2(400, 500);
			window.Show();
		}

		private void OnEnable() {
			EditorApplication.update += OnEditorUpdate;
			RefreshPlayerList();
		}

		private void OnDisable() {
			EditorApplication.update -= OnEditorUpdate;
		}

		private void OnEditorUpdate() {
			if (EditorApplication.timeSinceStartup - _lastRefresh < RefreshInterval)
				return;
			_lastRefresh = EditorApplication.timeSinceStartup;
			RefreshPlayerList();
			Repaint();
		}

		private void RefreshPlayerList() {
			var found = new List<Player>();
			for (int i = 0; i < SceneManager.sceneCount; i++) {
				var scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded) continue;
				foreach (var root in scene.GetRootGameObjects()) {
					found.AddRange(root.GetComponentsInChildren<Player>(true));
				}
			}
			_players = found.ToArray();
			if (_selectedIndex >= _players.Length)
				_selectedIndex = Mathf.Max(0, _players.Length - 1);
		}

		private void OnGUI() {
			DrawToolbar();

			if (_players.Length == 0) {
				EditorGUILayout.HelpBox("Aucun composant Player trouvé dans les scènes actives.", MessageType.Info);
				return;
			}

			var player = _players[_selectedIndex];
			if (!player) {
				EditorGUILayout.HelpBox("Le Player sélectionné est invalide.", MessageType.Warning);
				return;
			}

			_scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
			DrawPlayerSection(player);
			DrawClocksSection(player);
			DrawWorkersSection(player);
			DrawConnectorsSection(player);
			EditorGUILayout.EndScrollView();
		}

		// ──────────────────────────────────────────────────────────────────────
		// TOOLBAR
		// ──────────────────────────────────────────────────────────────────────

		private void DrawToolbar() {
			EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

			GUILayout.Label("Player :", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));

			var options = new string[_players.Length];
			for (int i = 0; i < _players.Length; i++) {
				var p = _players[i];
				options[i] = p
					? $"{p.gameObject.scene.name} / {GetGameObjectPath(p.gameObject)}"
					: "(null)";
			}

			_selectedIndex = EditorGUILayout.Popup(_selectedIndex, options, EditorStyles.toolbarPopup);

			if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
				RefreshPlayerList();

			if (_players.Length > 0 && _selectedIndex < _players.Length && _players[_selectedIndex]) {
				if (GUILayout.Button("Select", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
					Selection.activeGameObject = _players[_selectedIndex].gameObject;
			}

			EditorGUILayout.EndHorizontal();
		}

		// ──────────────────────────────────────────────────────────────────────
		// PLAYER SECTION
		// ──────────────────────────────────────────────────────────────────────

		private void DrawPlayerSection(Player player) {
			_foldPlayer = DrawFoldout(_foldPlayer, "Player", new Color(0.25f, 0.5f, 0.8f));
			if (!_foldPlayer) return;

			EditorGUI.indentLevel++;

			DrawRow("GameObject", GetGameObjectPath(player.gameObject));
			DrawRow("Scene", player.gameObject.scene.name);
			DrawRow("Active", player.gameObject.activeInHierarchy ? "✓ Active" : "✗ Inactive");
			DrawRow("State", FormatState(player.State));
			DrawRow("Is Playing", player.IsPlaying.ToString());
			DrawRow("Is Paused", player.IsPaused.ToString());
			DrawRow("Is Stream", player.IsStream.ToString());
			DrawRow("Loop", player.Loop.ToString());
			DrawRow("Playback Time", Application.isPlaying ? $"{player.PlaybackTime:F3} s" : "—");
			DrawRow("Length", Application.isPlaying ? $"{player.Length:F3} s" : "—");
		DrawRow("Time Offset", $"{player.timeOffset:F4}");
			DrawRow("Sync Worker Index", player.SyncWorkerIndex.ToString());
			DrawRow("Workers Count", player.Workers?.Length.ToString() ?? "—");

			EditorGUI.indentLevel--;
			GUILayout.Space(4);
		}

		// ──────────────────────────────────────────────────────────────────────
		// CLOCKS SECTION
		// ──────────────────────────────────────────────────────────────────────

		private void DrawClocksSection(Player player) {
			_foldClocks = DrawFoldout(_foldClocks, "Clocks", new Color(0.3f, 0.7f, 0.4f));
			if (!_foldClocks) return;

		EditorGUI.indentLevel++;
		DrawClockInfo("Master Clock", player.clock);

		if (player.Workers != null) {
			for (int i = 0; i < player.Workers.Length; i++) {
				var w = player.Workers[i];
				if (w == null) continue;
				var isMaster = i == player.SyncWorkerIndex;
				// worker.clock est protected internal — on affiche les infos disponibles sans y accéder
				EditorGUILayout.LabelField($"{w.GetType().Name} Clock{(isMaster ? " ★" : "")}", EditorStyles.boldLabel);
				EditorGUI.indentLevel++;
				DrawRow("Serial (pts)", w.serial.ToString());
				DrawRow("PTS", w.pts.ToString());
				DrawRow("Is Valid", w.IsValid.ToString());
				DrawRow("Is Master", isMaster.ToString());
				EditorGUI.indentLevel--;
				GUILayout.Space(2);
			}
		}

		EditorGUI.indentLevel--;
			GUILayout.Space(4);
		}

		private void DrawClockInfo(string label, Clock clock) {
			EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
			EditorGUI.indentLevel++;
			if (clock == null) {
				EditorGUILayout.LabelField("(non initialisée)");
				EditorGUI.indentLevel--;
				return;
			}
			if (Application.isPlaying) {
				DrawRow("Current Time", $"{clock.GetClock():F4} s");
				DrawRow("PTS", $"{clock.Pts:F4} s");
				DrawRow("Serial", clock.Serial.ToString());
				DrawRow("Speed", $"{clock.Speed:F2}x");
				DrawRow("Paused", clock.Paused.ToString());
				DrawRow("Obsolete", clock.IsObsolete().ToString());
			} else {
				DrawRow("State", "Disponible en Play Mode uniquement");
			}
			EditorGUI.indentLevel--;
			GUILayout.Space(2);
		}

		// ──────────────────────────────────────────────────────────────────────
		// WORKERS SECTION
		// ──────────────────────────────────────────────────────────────────────

		private void DrawWorkersSection(Player player) {
			_foldWorkers = DrawFoldout(_foldWorkers, $"Workers ({player.Workers?.Length ?? 0})", new Color(0.8f, 0.55f, 0.2f));
			if (!_foldWorkers) return;

			EditorGUI.indentLevel++;
			if (player.Workers == null || player.Workers.Length == 0) {
				EditorGUILayout.LabelField("Aucun worker configuré.");
				EditorGUI.indentLevel--;
				return;
			}

			for (int i = 0; i < player.Workers.Length; i++) {
				var w = player.Workers[i];
				DrawWorkerInfo(i, w, player);
			}

			EditorGUI.indentLevel--;
			GUILayout.Space(4);
		}

		private void DrawWorkerInfo(int index, BaseWorker worker, Player player) {
			if (worker == null) {
				EditorGUILayout.LabelField($"[{index}] (null)");
				return;
			}

			string key = $"{worker.GetEntityId().GetHashCode()}";
			if (!_workerFolds.ContainsKey(key))
				_workerFolds[key] = true;

			var isMaster = index == player.SyncWorkerIndex;
			var color    = worker.MediaType == AVMediaType.AVMEDIA_TYPE_AUDIO
				? new Color(0.4f, 0.7f, 1f)
				: new Color(1f, 0.6f, 0.6f);

			var title = $"[{index}] {worker.GetType().Name} — {worker.MediaType}{(isMaster ? " ★ Master" : "")}";
			_workerFolds[key] = DrawFoldout(_workerFolds[key], title, color, 1);
			if (!_workerFolds[key]) return;

			EditorGUI.indentLevel++;
			DrawRow("GameObject", worker.gameObject.name);
			DrawRow("Is Valid", worker.IsValid.ToString());
			DrawRow("Is Alive Thread", worker.IsAliveThread.ToString());
			DrawRow("Media Type", worker.MediaType.ToString());
			DrawRow("Offset", $"{worker.Offset:F4} s");
			DrawRow("Serial", worker.serial.ToString());
			DrawRow("PTS", worker.pts.ToString());
			DrawRow("Length", Application.isPlaying ? $"{worker.Length:F3} s" : "—");
			DrawRow("Is Ended", Application.isPlaying ? worker.IsEnded.ToString() : "—");

			if (worker is AudioWorker audio) {
				DrawRow("Volume", $"{audio.Volume:F2}");
				DrawRow("Buffer Size", $"{audio.bufferSize:F2} s");
			}
			else if (worker is VideoWorker video) {
				DrawRow("Dimensions", $"{video.Dimensions.x} × {video.Dimensions.y}");
				DrawRow("Force Width", $"{video.imageWidth}");
				DrawRow("Force Height", $"{video.imageHeight}");
				DrawRow("Flip Texture", video.flipTexture.ToString());
				DrawRow("Has Texture", (video.Texture != null).ToString());
			}

			EditorGUI.indentLevel--;
			GUILayout.Space(2);
		}

		// ──────────────────────────────────────────────────────────────────────
		// CONNECTORS SECTION
		// ──────────────────────────────────────────────────────────────────────

		/// <summary>
		/// Renvoie true si le connector est lié au Player donné :
		/// - il est enfant (direct ou indirect) du Player ou d'un de ses Workers
		/// - ou son champ worker pointe vers un Worker de ce Player
		/// - ou il est dans la même scène (fallback : on les liste tous avec leur Player)
		/// </summary>
		private static bool IsLinkedTo(Component connector, Player player) {
			// 1. Enfant hiérarchique du Player
			if (connector.transform.IsChildOf(player.transform))
				return true;

			// 2. Enfant hiérarchique d'un worker
			if (player.Workers != null)
				foreach (var w in player.Workers)
					if (w != null && connector.transform.IsChildOf(w.transform))
						return true;

			// 3. Référence explicite au worker (AudioSourceConnector)
			if (connector is AudioSourceConnector asc && asc.worker != null)
				if (player.Workers != null)
					foreach (var w in player.Workers)
						if (w == asc.worker) return true;

			// 4. Référence explicite au worker (MeshRendererConnector)
			if (connector is MeshRendererConnector mrc && mrc.worker != null)
				if (player.Workers != null)
					foreach (var w in player.Workers)
						if (w == mrc.worker) return true;

			return false;
		}

		private void DrawConnectorsSection(Player player) {
			// Scan toute la scène — les connectors ne sont pas forcément enfants du Player
			var audioConnectors = new List<AudioSourceConnector>();
			var meshConnectors  = new List<MeshRendererConnector>();
			var baseConnectors  = new List<BaseConnector>();

			for (int si = 0; si < SceneManager.sceneCount; si++) {
				var scene = SceneManager.GetSceneAt(si);
				if (!scene.isLoaded) continue;
				foreach (var root in scene.GetRootGameObjects()) {
					audioConnectors.AddRange(root.GetComponentsInChildren<AudioSourceConnector>(true));
					meshConnectors.AddRange(root.GetComponentsInChildren<MeshRendererConnector>(true));
					baseConnectors.AddRange(root.GetComponentsInChildren<BaseConnector>(true));
				}
			}

			// Filtrer ceux liés à ce Player et dédupliquer
			var seenAudio   = new HashSet<int>();
			var seenMesh    = new HashSet<int>();
			var seenBase    = new HashSet<int>();
			var uniqueAudio = audioConnectors.FindAll(c => c != null && seenAudio.Add(c.GetEntityId().GetHashCode()) && IsLinkedTo(c, player));
			var uniqueMesh  = meshConnectors.FindAll(c  => c != null && seenMesh.Add(c.GetEntityId().GetHashCode())  && IsLinkedTo(c, player));
			// BaseConnector exclut AudioSourceConnector (déjà compté) et filtre par Player
			var uniqueBase  = baseConnectors.FindAll(c  => c != null && seenBase.Add(c.GetEntityId().GetHashCode())
			                                               && !(c is AudioSourceConnector)
			                                               && IsLinkedTo(c, player));

			int total = uniqueAudio.Count + uniqueMesh.Count + uniqueBase.Count;

			_foldConnectors = DrawFoldout(_foldConnectors, $"Connectors ({total})", new Color(0.7f, 0.4f, 0.8f));
			if (!_foldConnectors) return;

			EditorGUI.indentLevel++;
			if (total == 0) {
				EditorGUILayout.HelpBox(
					"Aucun connector directement lié à ce Player.\n" +
					"Les connectors doivent être enfants du Player/Worker, ou référencer un de ses Workers.",
					MessageType.Warning);

				// Affiche quand même tous les connectors de la scène pour aider au diagnostic
				if (seenAudio.Count > 0 || seenMesh.Count > 0) {
					EditorGUILayout.LabelField("— Connectors présents dans la scène —", EditorStyles.miniLabel);
					var allAudio = audioConnectors.FindAll(c => c != null);
					var allMesh  = meshConnectors.FindAll(c  => c != null);
					foreach (var c in allAudio) DrawAudioConnectorInfo(c, unlinked: true);
					foreach (var c in allMesh)  DrawMeshConnectorInfo(c, unlinked: true);
				}

				EditorGUI.indentLevel--;
				return;
			}

			foreach (var c in uniqueAudio) DrawAudioConnectorInfo(c);
			foreach (var c in uniqueMesh)  DrawMeshConnectorInfo(c);
			foreach (var c in uniqueBase)  DrawBaseConnectorInfo(c);

			EditorGUI.indentLevel--;
			GUILayout.Space(4);
		}

		private void DrawAudioConnectorInfo(AudioSourceConnector audio, bool unlinked = false) {
			var label = unlinked ? "AudioSourceConnector  ⚠ non lié" : "AudioSourceConnector";
			var style = unlinked ? GetWarningBoldStyle() : EditorStyles.boldLabel;
			EditorGUILayout.LabelField(label, style);
			EditorGUI.indentLevel++;
			DrawRow("GameObject", GetGameObjectPath(audio.gameObject));
			DrawRow("Active", audio.gameObject.activeInHierarchy ? "✓ Active" : "✗ Inactive");
			DrawRow("Worker", audio.worker ? audio.worker.gameObject.name : "(none)");
			DrawRow("Buffer Delay", $"{audio.bufferDelay:F3} s");
			DrawRow("AudioSource", audio.audioSource ? "✓ Assigned" : "✗ Missing");
			if (audio.audioSource && Application.isPlaying) {
				DrawRow("Is Playing", audio.audioSource.isPlaying.ToString());
				DrawRow("Volume", $"{audio.audioSource.volume:F2}");
				DrawRow("Muted", audio.audioSource.mute.ToString());
				DrawRow("Time", $"{audio.audioSource.time:F3} s");
			}
			EditorGUI.indentLevel--;
			GUILayout.Space(2);
		}

		private void DrawMeshConnectorInfo(MeshRendererConnector mesh, bool unlinked = false) {
			var label = unlinked ? "MeshRendererConnector  ⚠ non lié" : "MeshRendererConnector";
			var style = unlinked ? GetWarningBoldStyle() : EditorStyles.boldLabel;
			EditorGUILayout.LabelField(label, style);
			EditorGUI.indentLevel++;
			DrawRow("GameObject", GetGameObjectPath(mesh.gameObject));
			DrawRow("Active", mesh.gameObject.activeInHierarchy ? "✓ Active" : "✗ Inactive");
			DrawRow("Worker", mesh.worker ? mesh.worker.gameObject.name : "(none)");
			DrawRow("Material Index", mesh.materialIndex == -1 ? "All" : mesh.materialIndex.ToString());
			DrawRow("Texture Properties", string.Join(", ", mesh.textureProperties));
			DrawRow("Update GI Realtime", mesh.updateGIRealtime.ToString());
			EditorGUI.indentLevel--;
			GUILayout.Space(2);
		}

		private void DrawBaseConnectorInfo(BaseConnector connector) {
			EditorGUILayout.LabelField(connector.GetType().Name, EditorStyles.boldLabel);
			EditorGUI.indentLevel++;
			DrawRow("GameObject", GetGameObjectPath(connector.gameObject));
			DrawRow("Active", connector.gameObject.activeInHierarchy ? "✓ Active" : "✗ Inactive");
			EditorGUI.indentLevel--;
			GUILayout.Space(2);
		}

		// ──────────────────────────────────────────────────────────────────────
		// HELPERS
		// ──────────────────────────────────────────────────────────────────────

		private static GUIStyle _warningBoldStyle;
		private static GUIStyle GetWarningBoldStyle() {
			if (_warningBoldStyle != null) return _warningBoldStyle;
			_warningBoldStyle = new GUIStyle(EditorStyles.boldLabel) {
				normal = { textColor = new Color(1f, 0.6f, 0.1f) }
			};
			return _warningBoldStyle;
		}

		private static bool DrawFoldout(bool state, string title, Color accentColor, int extraIndent = 0) {
			var rect = EditorGUILayout.BeginVertical();
			EditorGUI.DrawRect(new Rect(rect.x + extraIndent * 15, rect.y, 3, 18), accentColor);
			state = EditorGUILayout.Foldout(state, "  " + title, true, EditorStyles.foldoutHeader);
			EditorGUILayout.EndVertical();
			return state;
		}

		private static void DrawRow(string label, string value) {
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField(label, GUILayout.Width(160));
			EditorGUILayout.LabelField(value, EditorStyles.label);
			EditorGUILayout.EndHorizontal();
		}

		private static string FormatState(PlayState state) => state switch {
			PlayState.Playing   => "▶ Playing",
			PlayState.Paused    => "⏸ Paused",
			PlayState.Stopped   => "⏹ Stopped",
			PlayState.Buffering => "⏳ Buffering",
			PlayState.Stalled   => "⚠ Stalled",
			PlayState.Ended     => "✓ Ended",
			_ => state.ToString()
		};

		private static string GetGameObjectPath(GameObject go) {
			if (!go) return "(null)";
			var path  = go.name;
			var trans = go.transform.parent;
			while (trans != null) {
				path  = trans.name + "/" + path;
				trans = trans.parent;
			}
			return path;
		}
	}
}








