using UnityEngine;

namespace Nox.FFmpeg {
	/// <summary>
	/// Clock system for synchronization, similar to ffplay.c
	/// Manages PTS (Presentation Time Stamp) and serial numbers for discontinuity detection
	/// </summary>
	public class Clock {
		private double pts;
		private double lastUpdated;
		private double ptsDrift;
		private int serial;
		private double speed = 1.0;
		private bool paused;

		// Reference to queue serial for detecting obsolete clocks
		private int? queueSerial;

		public double Pts
			=> pts;
		public int Serial
			=> serial;
		public double Speed
			=> speed;
		public bool Paused
			=> paused;

		/// <summary>
		/// Set clock at specific time with serial number
		/// </summary>
		public void SetClockAt(double pts, int serial, double time) {
			this.pts         = pts;
			this.lastUpdated = time;
			this.ptsDrift    = pts - time;
			this.serial      = serial;
		}

		/// <summary>
		/// Set clock with current time
		/// </summary>
		public void SetClock(double pts, int serial) {
			double time = Player.timeAsDouble;
			SetClockAt(pts, serial, time);
		}

		/// <summary>
		/// Get current clock value
		/// </summary>
		public double GetClock() {
			if (paused)
				return pts;

			double time = Player.timeAsDouble;
			return ptsDrift + time - (time - lastUpdated) * (1.0 - speed);
		}

		/// <summary>
		/// Set clock speed (for playback rate control)
		/// </summary>
		public void SetClockSpeed(double speed) {
			SetClock(GetClock(), serial);
			this.speed = speed;
		}

		/// <summary>
		/// Initialize clock
		/// </summary>
		public void Init(int? queueSerial = null) {
			this.speed       = 1.0;
			this.paused      = false;
			this.queueSerial = queueSerial;
			SetClock(double.NaN, -1);
		}

		/// <summary>
		/// Synchronize this clock to a slave clock if they differ significantly
		/// </summary>
		public void SyncToSlave(Clock slave, double threshold = 10.0) {
			double clock      = GetClock();
			double slaveClock = slave.GetClock();

			if (!double.IsNaN(slaveClock) &&
				(double.IsNaN(clock) || System.Math.Abs(clock - slaveClock) > threshold)) {
				SetClock(slaveClock, slave.Serial);
			}
		}

		/// <summary>
		/// Check if clock is obsolete based on queue serial
		/// </summary>
		public bool IsObsolete() {
			return queueSerial.HasValue && serial != queueSerial.Value;
		}

		public void SetPaused(bool paused) {
			this.paused = paused;
		}
	}
}