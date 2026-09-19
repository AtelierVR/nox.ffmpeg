using System;
using System.Linq;
using Nox.CCK.VideoPlayer;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.VideoPlayer;
using UnityEngine;
using UnityEngine.Events;
using Cysharp.Threading.Tasks;
using LogType = Nox.CCK.Utils.LogType;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.FFmpeg {
	public partial class Player : MonoBehaviour, IVideoPlayer, 
		IVideoPlayerVideo, IVideoPlayerDetails, 
		IVideoPlayerResolution, IVideoPlayerAudio 
	{
		[Header("Playback")]
		public string Url;
		public bool AutoPlay = true;
		public int AvSyncType = Constants.AV_SYNC_AUDIO_MASTER;

		// ── State (IVideoPlayer implementation) ───────────────────────────
		public PlayerState InternalState;
		public UnityEvent<PlayerState> OnStateChanged { get; } = new();

		public State State {
			get {
				if (InternalState == null)
					return State.Stopped;
				if (IsBuffering)
					return State.Buffering;
				if (InternalState.Paused)
					return State.Paused;
				if (InternalState.Eof && InternalState.HasReachedEnd)
					return State.Ended;
				return State.Playing;
			}
		}

		public UnityEvent<IVideoPlayer, State> OnState { get; } = new();

		public bool IsPlaying => State == State.Playing;
		public bool IsPaused => State == State.Paused;
		public bool IsBuffering => InternalState != null && !InternalState.Handlers.All(h => h.HasEnoughPackets);

		// ── Unity lifecycle ───────────────────────────────────────────────
		private void Awake() 
			=> Initializer.Initialize();

		private void Start() {
			VideoPlayerRegister.Register(this);
			if (AutoPlay && !string.IsNullOrWhiteSpace(Url))
				Play(Url);
		}

		private void Update()
			=> InternalState?.Update();

		private void OnDestroy() {
			VideoPlayerRegister.UnRegister(this);
			Close();
		}
		
		private void OnDisable()
			=> InternalState?.TogglePause();
		
		private void OnEnable() {
			if (InternalState is { Paused: true })
				InternalState.TogglePause();
		}

		internal void Log(LogType type, string message) {
			UniTask.Post(() => {
				Logger.Print(type, message, this, name);
				OnMessage.Invoke(this, type, message);
			});
		}
	}
}