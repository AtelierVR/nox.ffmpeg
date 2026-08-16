using System;
using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Utils {
	
	// ─────────────────────────────────────────────────────────────────────────
	// Clock — identical to ffplay.c Clock
	// ─────────────────────────────────────────────────────────────────────────
	public class Clock {
		public double Pts { get; private set; }
		public double PtsDrift { get; private set; }
		public double LastUpdated { get; private set; }
		public double Speed { get; private set; } = 1.0;
		public int Serial { get; private set; } = -1;
		public bool Paused { get; set; }

		// pointer to the owning queue's serial (C# equivalent: getter delegate)
		private readonly Func<int> _queueSerial;

		public Clock(Func<int> queueSerial) {
			_queueSerial = queueSerial;
			Set(double.NaN, -1);
		}

		// get_clock
		public double Get() {
			if (_queueSerial() != Serial)
				return double.NaN;
			if (Paused)
				return Pts;
			var time = AvGetTimeRelative();
			return PtsDrift + time - (time - LastUpdated) * (1.0 - Speed);
		}

		// set_clock_at
		public void SetAt(double pts, int serial, double time) {
			Pts         = pts;
			LastUpdated = time;
			PtsDrift    = pts - time;
			Serial      = serial;
		}

		// set_clock
		public void Set(double pts, int serial)
			=> SetAt(pts, serial, AvGetTimeRelative());

		// set_clock_speed
		public void SetSpeed(double speed) {
			Set(Get(), Serial);
			Speed = speed;
		}

		// sync_clock_to_slave
		public void SyncToSlave(Clock slave) {
			double c = Get(),
				s    = slave.Get();
			if (!double.IsNaN(s) && (double.IsNaN(c) || Math.Abs(c - s) > Constants.AV_NOSYNC_THRESHOLD))
				Set(s, slave.Serial);
		}

		private static double AvGetTimeRelative()
			=> ffmpeg.av_gettime_relative() / 1_000_000.0;
	}
}