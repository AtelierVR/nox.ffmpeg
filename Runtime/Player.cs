using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Sources;
using Nox.FFmpeg.Workers;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg {
	public class Player : MonoBehaviour {
		public BaseWorker[] Workers;

		public readonly Clock clock = new();
		public int SyncWorkerIndex = 0;
		public bool Loop = false;

		// Serial number incremented on seek/discontinuity
		internal int packetSerial = 0;

		public T GetWorker<T>() where T : BaseWorker {
			foreach (var worker in Workers)
				if (worker is T tWorker)
					return tWorker;
			return null;
		}

		public bool TryGetWorker<T>(out T worker) where T : BaseWorker {
			worker = GetWorker<T>();
			return worker;
		}

		public UnityEvent<double> OnSeeked = new();
		public UnityEvent<Exception> OnError = new();
		public UnityEvent<bool> OnLooping = new();
		public UnityEvent<PlayState> OnPlayState = new();
		public UnityEvent<LogType, string> OnMessage = new();







		public delegate void OnEndReachedDelegate();


		public delegate void OnMediaReadyDelegate();

		public event OnEndReachedDelegate OnEndReached;
		public event OnMediaReadyDelegate OnMediaReady;

		public double timeOffset = 0d;

		private double pauseTime = 0d;

		public bool IsPlaying { get; private set; } = false;
		public bool IsStream { get; internal set; } = false;

		public bool IsPaused { get; private set; } = false;

		public static double timeAsDouble
			=> AudioSettings.dspTime;

		public double PlaybackTime
			=> IsPaused ? pauseTime : timeAsDouble - timeOffset;
		
		private PlayState _currentState = PlayState.Stopped;
		
		public PlayState State {
			get => _currentState;
			private set {
				if (_currentState == value)
					return;
				_currentState = value;
				OnPlayState.Invoke(value);
			}
		}

		public void Play(string url)
			=> Play(new Reader(new UrlSource(url), new[] {
				AVMediaType.AVMEDIA_TYPE_AUDIO,
				AVMediaType.AVMEDIA_TYPE_VIDEO
			}));

		public void Play(params Reader[] timings) {
			IsPlaying = false;

			StopThread();
			OnDestroy();

			foreach (var timing in timings)
			foreach (var worker in Workers) {
				if (!timing.Medias.ContainsKey(worker.MediaType))
					continue;

				worker.Timings = timing;
			}

			Init();
		}

		private static Reader[] CreateTimings(params (AVMediaType type, Stream stream)[] inputs)
			=> inputs
				.GroupBy(i => i.stream)
				.Select(g => new Reader(new StreamSource(g.Key), g.Select(i => i.type).ToArray()))
				.ToArray();

		private static Reader[] CreateTimings(params (AVMediaType type, string url)[] inputs)
			=> inputs
				.GroupBy(i => i.url)
				.Select(g => new Reader(new UrlSource(g.Key), g.Select(i => i.type).ToArray()))
				.ToArray();

		public void Play(string urlV, string urlA, string urlS = null)
			=> Play(CreateTimings(
				(AVMediaType.AVMEDIA_TYPE_VIDEO, urlV),
				(AVMediaType.AVMEDIA_TYPE_AUDIO, urlA),
				(AVMediaType.AVMEDIA_TYPE_SUBTITLE, urlS)
			));

		public void Play(Stream streamV, Stream streamA)
			=> Play(CreateTimings(
				(AVMediaType.AVMEDIA_TYPE_VIDEO, streamV),
				(AVMediaType.AVMEDIA_TYPE_AUDIO, streamA),
				(AVMediaType.AVMEDIA_TYPE_SUBTITLE, streamA)
			));


		private void Init() {
			clock.Init(packetSerial);

			var found = false;
			foreach (var worker in Workers) {
				worker.Init();

				// Connect buffering/stalled events
				worker.OnBuffering.AddListener(HandleBuffering);
				worker.OnStalled.AddListener(HandleStalled);

				if (worker.Timings is not { IsValid: true })
					continue;

				var media = worker.Timings.Medias[worker.MediaType];

				timeOffset = timeAsDouble - media.StartTime;
				IsStream   = Math.Abs(media.StartTime) > 5d;
				found      = true;
			}

			if (!found)
				timeOffset = timeAsDouble;

			if (Workers.Length == 0 || Workers.All(w => !w.IsValid)) {
				IsPaused = true;
				StopThread();
				IsPlaying = false;
				State = PlayState.Stopped;
				OnError?.Invoke(new Exception("AV not found"));
				return;
			}

			OnMediaReady?.Invoke();
			foreach (var worker in Workers)
				worker.Resume();

			RunThread();
			IsPlaying = true;
			State = PlayState.Playing;
		}


		public void Seek(double timestamp) {
			if (IsStream)
				return;

			StopThread();

			// Increment serial on seek to detect discontinuities
			packetSerial++;

			timeOffset = timeAsDouble - timestamp;
			pauseTime  = timestamp;

			foreach (var worker in Workers)
				worker.Seek();

			// Reset clocks with new serial
			foreach (var worker in Workers)
				worker.clock.SetClock(timestamp, packetSerial);
			clock.SetClock(timestamp, packetSerial);

			RunThread();

			// Notify that seek is complete
			OnSeeked?.Invoke(timestamp);
		}

		public double Length
			=> (from w in Workers where w.IsValid select w.Length)
				.FirstOrDefault();

		public bool IsLooping {
			get => Loop;
			set {
				Loop = value;
				OnLooping?.Invoke(value);
			}
		}

		public void Pause() {
			if (IsPaused)
				return;
			pauseTime = PlaybackTime;
			foreach (var worker in Workers)
				worker.Pause();
			IsPaused = true;
			StopThread();
			IsPlaying = false;
			State = PlayState.Paused;
		}

		public void Resume() {
			if (!IsPaused)
				return;
			StopThread();
			timeOffset = timeAsDouble - pauseTime;
			foreach (var worker in Workers)
				worker.Resume();
			IsPaused = false;
			RunThread();
			IsPlaying = true;
			State = PlayState.Playing;
		}

		private void Update() {
			if (IsPaused)
				return;

			// Synchronize clocks periodically
			SyncClocks();

		}

		private void OnDestroy() {
			StopThread();
		}

		private void RunThread() {
			if (Workers.Any(w => w.IsAliveThread))
				throw new Exception("Worker thread is already running");

			Debug.Log("Starting worker threads");
			IsPaused = false;

			foreach (var worker in Workers)
				worker.StartThread();
		}

		private void StopThread() {
			var paused = IsPaused;
			Debug.Log("Stopping worker threads");
			IsPaused = true;
			foreach (var worker in Workers)
				worker.StopThread();
			IsPaused = paused;
		}

		public void OnDisable() {
			IsPaused = true;
			OnDestroy();
		}

		/// <summary>
		/// Get master clock based on sync type
		/// </summary>
		private Clock GetMasterClock() {
			if (SyncWorkerIndex < 0 || SyncWorkerIndex >= Workers.Length)
				return clock;
			
			var worker = Workers[SyncWorkerIndex];
			if (worker && worker.IsValid && worker.clock != null)
				return worker.clock;
			return clock;
		}

		/// <summary>
		/// Synchronize clocks to master
		/// </summary>
		private void SyncClocks() {
			var master = GetMasterClock();
			clock.SyncToSlave(master);
		}

		public void Stop() {
			StopThread();
			OnDestroy();
			IsPlaying = false;
			State = PlayState.Stopped;
		}

		private void HandleBuffering(bool isBuffering) {
			if (isBuffering)
				State = PlayState.Buffering;
			else if (IsPlaying && !IsPaused)
				State = PlayState.Playing;
			else if (IsPaused)
				State = PlayState.Paused;
		}

		private void HandleStalled(bool isStalled) {
			if (isStalled)
				State = PlayState.Stalled;
			else if (IsPlaying && !IsPaused)
				State = PlayState.Playing;
			else if (IsPaused)
				State = PlayState.Paused;
		}
	}
}