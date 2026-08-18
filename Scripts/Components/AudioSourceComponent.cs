using System;
using UnityEngine;
using Nox.FFmpeg.Handlers;

namespace Nox.FFmpeg.Components {

	/// Subscribes to a Player's OnClip event and drives one or more AudioSources
	/// with the resulting PCM stream. Mirrors VoiceAudioSourceOutput: the non-stream
	/// circular AudioClip is pre-filled to the target latency, then kept ahead of the
	/// AudioSource read cursor by pulling decoded PCM from the Player on the main thread.
	[AddComponentMenu("Nox/FFmpeg/Audio Source")]
	public class AudioSourceComponent : MonoBehaviour {

		[Header("Source")]
		[Tooltip("Player to subscribe to. Leave null to search on the same or parent GameObject.")]
		public Player Source;

		[Header("Targets")]
		[Tooltip("AudioSources to drive. Leave empty to auto-find AudioSources on this GameObject.")]
		public AudioSource[] Targets = Array.Empty<AudioSource>();

		[Header("Buffering")]
		[Tooltip("Seconds of PCM to buffer before starting playback (jitter buffer).")]
		[Range(0.02f, 1f)]
		public float TargetLatency = 0.15f;

		private AudioClip _clip;
		private int _writePos;   // sample-frames written into the clip (monotonic)
		private bool _started;
		private float[] _scratch;

		private const int FillChunkFrames = 4096;

		// ── Utils ───────────────────────────────────────────────────────── 

		/// Current PCM write cursor in the ring buffer (sample-frames mod clip.samples).
		public AudioHandler Audio 
			=> Source.State.GetHandler<AudioHandler>();


		// ── Lifecycle ──────────────────────────────────────────────────────

		private void Awake() {
			if (!Source)
				Source = GetComponentInParent<Player>(includeInactive: true);
			if (Targets.Length == 0)
				Targets = GetComponents<AudioSource>();
			foreach (var a in Targets)
				if (a) a.playOnAwake = false;
		}

		private void OnEnable() {
			if(Source.State != null)
				HandleStateChanged(Source.State);
			Source.OnStateChanged.AddListener(HandleStateChanged);
		}

		private void HandleStateChanged(PlayerState state) {
			if (Audio == null) return;
			Source.OnClip.AddListener(HandleClip);
			HandleClip(Audio.Clip);
		}

		private void OnDisable() {
			Audio?.OnClip.RemoveListener(HandleClip);
			Source.OnStateChanged.RemoveListener(HandleStateChanged);
			foreach (var a in Targets)
				if (a) a.Pause();
		}

		private void OnDestroy() {
			foreach (var a in Targets) {
				if (!a) continue;
				a.Stop();
				a.clip = null;
			}
			_clip = null;
		}

		// ── Clip handler ──────────────────────────────────────────────────

		private void HandleClip(AudioClip clip) {
			if (!clip) {
				_clip = null;
				foreach (var a in Targets)
					if (a) a.Stop();
				return;
			}
			_clip = clip;
			_writePos = 0;
			_started = false;
			_scratch = null;
			foreach (var a in Targets) {
				if (!a) continue;
				a.playOnAwake = false;
				a.clip = clip;
				a.loop = true;
				a.Stop();
			}
		}

		// ── Main-thread pump ──────────────────────────────────────────────

		private void Update() {
			if (!Source || !_clip) return;

			int clipLen    = _clip.samples;
			int channels   = _clip.channels;
			int sampleRate = _clip.frequency;
			if (clipLen <= 0 || channels <= 0 || sampleRate <= 0) return;

			// Only audible while actually playing: not paused, not buffering,
			// and the player state is alive.
			bool shouldPlay = Source.IsPlaying && !Source.IsBuffering;

			if (!shouldPlay) {
				foreach (var a in Targets)
					if (a && a.isPlaying)
						a.Pause();
				return;
			}

			// Read cursor = furthest-along playing target (timeSamples wraps at clipLen).
			int readPos = 0;
			if (_started) {
				foreach (var a in Targets) {
					if (!a) continue;
					if (!a.isPlaying) { a.Play(); continue; }
					readPos = Mathf.Max(readPos, a.timeSamples);
				}
			}

			// Ring distance from the read cursor to the write cursor.
			int ahead = (_writePos % clipLen) - readPos;
			if (ahead < 0) ahead += clipLen;

			if (!_started) {
				// Pre-fill the ring to TargetLatency before playback starts.
				int targetFrames = Mathf.CeilToInt(TargetLatency * sampleRate);
				if (_writePos < targetFrames) {
					Fill(clipLen, channels, Mathf.Min(targetFrames - _writePos, clipLen));
				} else {
					foreach (var a in Targets) {
						if (!a) continue;
						a.time = 0f;
						a.Play();
					}
					_started = true;
				}
				return;
			}

			// Keep the ring filled ahead of the slowest reader; never overwrite it.
			int wantAhead = Mathf.CeilToInt(TargetLatency * sampleRate);
			if (ahead < wantAhead) {
				int free = clipLen - ahead;   // frames we may still write this frame
				if (free > 0)
					Fill(clipLen, channels, free);
			}

			// Feed the total decode→speaker buffering back to the A/V clock.
			int decodedAhead = Audio.PcmWritePos - _writePos;   // frames still in the handler ring
			float totalLatency = (float)(decodedAhead + ahead) / sampleRate;
			Source.SetAudioLatency(Mathf.Clamp(totalLatency, 0.01f, 1.5f));
		}

		private void Fill(int clipLen, int channels, int want) {
			int chunk = Mathf.Min(want, FillChunkFrames);
			if (chunk <= 0) return;

			int need = chunk * channels;
			if (_scratch == null || _scratch.Length < need)
				_scratch = new float[need];

			int got = Audio.Read(_scratch, chunk);
			if (got <= 0) return;

			int frameIndex = _writePos % clipLen;
			int first = Mathf.Min(got, clipLen - frameIndex);

			var data = new float[first * channels];
			Array.Copy(_scratch, 0, data, 0, data.Length);
			// SetData's offsetSamples is a per-channel sample (frame) index, not
			// an interleaved float index — for stereo we must NOT multiply by channels.
			_clip.SetData(data, frameIndex);

			if (first < got) {
				int rest = got - first;
				var data2 = new float[rest * channels];
				Array.Copy(_scratch, first * channels, data2, 0, data2.Length);
				_clip.SetData(data2, 0);
			}

			_writePos += got;
		}
	}
}
