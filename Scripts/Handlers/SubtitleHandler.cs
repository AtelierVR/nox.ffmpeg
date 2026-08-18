using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using Nox.FFmpeg;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Handlers {
	/// Handles subtitle packets/frames. No rendering in this port.
	public unsafe sealed class SubtitleHandler : IHandler {
		public override StreamType Type => StreamType.Subtitle;

		// ── FFmpeg demux/decode state (owned by this handler) ────────────
		public PacketQueue SubtitleQ { get; } = new();
		public FrameQueue  SubpQ     { get; }
		public Decoder     SubDec;

		public SubtitleHandler() {
			SubpQ = new FrameQueue(SubtitleQ, 16, false);
		}

		public override bool HasEnded
			=> StreamPtr == null || (SubDec != null && SubDec.Finished == SubtitleQ.Serial && SubpQ.NbRemaining() == 0);

		public override bool HasEnoughPackets => true;

		public override void Start() => IsRunning = true;
		public override void Stop()  => IsRunning = false;
	}
}
