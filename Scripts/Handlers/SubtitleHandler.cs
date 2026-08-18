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
	/// Placeholder handler for subtitles.
	public sealed class SubtitleHandler : IHandler {
		public override StreamType Type => StreamType.Subtitle;

		public override void Start() => IsRunning = true;
		public override void Stop()  => IsRunning = false;
	}
}
