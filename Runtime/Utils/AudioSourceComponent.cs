using System;
using UnityEngine;

namespace Nox.FFmpeg.Utils {

	/// Subscribes to a Player's OnClip event and drives one or more AudioSources
	/// with the resulting streaming AudioClip, keeping them synchronized.
	[AddComponentMenu("Nox/FFmpeg/Audio Source")]
	public class AudioSourceComponent : MonoBehaviour {

		[Header("Source")]
		[Tooltip("Player to subscribe to. Leave null to search on the same or parent GameObject.")]
		public Player Source;

		[Header("Targets")]
		[Tooltip("AudioSources to drive. Leave empty to auto-find AudioSources on this GameObject.")]
		public AudioSource[] Targets = Array.Empty<AudioSource>();

		// ── Lifecycle ──────────────────────────────────────────────────────

		private void Awake() {
			if (!Source)
				Source = GetComponentInParent<Player>(includeInactive: true);
			if (Targets.Length == 0)
				Targets = GetComponents<AudioSource>();
		}

		private void OnEnable() {
			if (!Source) return;
			Source.OnClip.AddListener(HandleClip);
			// Apply current clip immediately if one is already playing
			if (Source.Clip)
				HandleClip(Source.Clip);
		}

		private void OnDisable() {
			if (Source)
				Source.OnClip.RemoveListener(HandleClip);
			foreach (var a in Targets)
				if (a) a.Pause();
		}

		private void OnDestroy() {
			foreach (var a in Targets) {
				if (!a) continue;
				a.Stop();
				a.clip = null;
			}
		}

		// Restart any target that stopped unexpectedly while the stream is still playing.
		// Also measures real PCM buffer depth and feeds it back to the video clock.
		private void Update() {
			if (!Source || !Source.Clip || !Source.IsPlaying) return;
			int clipLen = Source.Clip.samples; // == AudioSampleRate (1-second ring)
			for (int i = 0; i < Targets.Length; i++) {
				var a = Targets[i];
				if (!a) continue;
				if (!a.isPlaying && a.clip == Source.Clip) { a.Play(); continue; }
				// First playing target: measure write-cursor vs read-cursor to get true latency
				if (a.isPlaying && a.clip == Source.Clip) {
					int depth = (Source.PcmWritePos - a.timeSamples + clipLen) % clipLen;
					Source.SetAudioLatency((double)depth / Source.AudioSampleRate);
					break;
				}
			}
		}

		// ── Clip handler ──────────────────────────────────────────────────

		private void HandleClip(AudioClip clip) {
			foreach (var a in Targets) {
				if (!a) continue;
				a.clip = clip;
				a.loop = true;
				if (clip != null)
					a.Play();
				else
					a.Stop();
			}
		}
	}
}
