using System;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Sources;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Workers {
	public class BaseWorker : MonoBehaviour {
		[Header("Settings")]
		public double Offset = 0d;
		public Player Context;

		[Header("Runtime Data")]
		public long pts;
		public int serial = -1;

		protected Reader timings;
		protected Thread thread;
		protected internal Clock clock;

		public virtual AVMediaType MediaType
			=> AVMediaType.AVMEDIA_TYPE_UNKNOWN;

		protected double Time
			=> Player.timeAsDouble - Context.timeOffset + Offset;

		protected int Index
			=> Array.IndexOf(Context.Workers, this);

		public readonly UnityEvent OnSeek = new();
		public readonly UnityEvent OnPause = new();
		public readonly UnityEvent OnResume = new();
		public readonly UnityEvent<bool> OnBuffering = new();
		public readonly UnityEvent<bool> OnStalled = new();

		public virtual Reader Timings {
			get => timings;
			set {
				// Disconnect old events
				if (timings != null) {
					timings.OnBufferingStart -= HandleBufferingStart;
					timings.OnBufferingEnd   -= HandleBufferingEnd;
					timings.OnStalledStart   -= HandleStalledStart;
					timings.OnStalledEnd     -= HandleStalledEnd;
				}

				timings?.Dispose();
				timings = value;

				// Connect new events
				if (timings != null) {
					timings.OnBufferingStart += HandleBufferingStart;
					timings.OnBufferingEnd   += HandleBufferingEnd;
					timings.OnStalledStart   += HandleStalledStart;
					timings.OnStalledEnd     += HandleStalledEnd;
				}

				Debug.Log($"Initialized {GetType().Name} with timings: {value}");
			}
		}

		private void HandleBufferingStart()
			=> OnBuffering.Invoke(true);

		private void HandleBufferingEnd()
			=> OnBuffering.Invoke(false);

		private void HandleStalledStart()
			=> OnStalled.Invoke(true);

		private void HandleStalledEnd()
			=> OnStalled.Invoke(false);


		public virtual void Init() {
			clock = new Clock();
			clock.Init(Context.packetSerial);
			Debug.Log($"Initialized {GetType().Name}");
		}

		public virtual bool IsValid
			=> timings?.IsValid ?? false;

		public virtual double Length
			=> timings?.Length ?? 0d;

		public virtual bool IsEnded
			=> timings?.IsEnded ?? true;

		public virtual bool IsAliveThread
			=> thread is { IsAlive: true };

		public virtual void Seek() {
			if (timings == null)
				return;
			timings.Seek(MediaType, Time);
			OnSeek.Invoke();
		}

		public virtual void Dispose()
			=> timings?.Dispose();

		public void StartThread() {
			if (IsAliveThread)
				return;

			thread = new Thread(ThreadUpdate) {
				Name = $"{GetType().Name} Thread"
			};

			thread.Start();
		}

		virtual protected void ThreadUpdate() {
			// Override in derived classes
		}

		public virtual void StopThread() {
			if (!IsAliveThread)
				return;

			thread.Join();
		}

		public virtual void Pause() {
			if (clock == null)
				return;
			clock.SetPaused(true);
			OnPause.Invoke();
		}

		public virtual void Resume() {
			if (clock == null)
				return;
			clock.SetPaused(false);
			OnResume.Invoke();
		}

		/// <summary>
		/// Update clock with new PTS value
		/// </summary>
		protected void UpdateClock(double p, int s) {
			if (clock == null)
				return;
			clock.SetClock(p, s);
			serial = s;
		}
	}
}