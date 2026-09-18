using System;
using Nox.FFmpeg.Handlers;
using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg {
	public partial class Player {
		private float _volume = 1f;
		private bool _muted;

		// ── Subscriptions & Events ────────────────────────────────────────
		public event Action<float[], int, int> OnAudioSamples;

		public UnityEvent<IVideoPlayer, AudioClip> OnClip { get; } = new();
		public UnityEvent<IVideoPlayer, float> OnVolume { get; } = new();
		public UnityEvent<IVideoPlayer, bool> OnMute { get; } = new();

		// ── Audio Properties ──────────────────────────────────────────────
		public AudioClip Clip => InternalState?.GetHandler<AudioHandler>()?.Clip;
		public int AudioSampleRate => InternalState?.GetHandler<AudioHandler>()?.SampleRate ?? 0;

		public float Volume {
			get => InternalState != null ? InternalState.Audio.AudioVolume / 128f : _volume;
			set {
				_volume = Mathf.Clamp01(value);
				if (InternalState != null)
					InternalState.Audio.AudioVolume = _muted ? 0 : (int)(_volume * 128);
				OnVolume.Invoke(this, _volume);
			}
		}

		public bool Muted {
			get => _muted;
			set {
				if (_muted == value) return;
				_muted = value;
				if (InternalState != null)
					InternalState.Audio.AudioVolume = _muted ? 0 : (int)(_volume * 128);
				OnMute.Invoke(this, _muted);
			}
		}
	}
}