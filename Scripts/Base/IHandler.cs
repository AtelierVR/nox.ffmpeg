using System;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Utils;
using ITrack = Nox.VideoPlayer.ITrack;

namespace Nox.FFmpeg.Base {
	/// Base class for typed processors that manage the streams of their <see cref="Type"/>.
	public abstract unsafe class IHandler : IDisposable {


		public IHandler(PlayerState state) 
			=> State = state;

		public virtual void Dispose() {
			Packets?.Dispose();
			Frames?.Dispose();
		}

		public PlayerState State { get; set; }

		public abstract StreamType Type { get; }
		public abstract AVMediaType[] MediaTypes { get; }

		/// True once <see cref="Start"/> has run and <see cref="Stop"/> hasn't.
		public bool IsRunning { get; protected set; }

		/// FFmpeg stream index selected by the demuxer (-1 until opened).
		public int StreamIndex { get; set; } = -1;
		public AVStream* StreamPtr { get; set; }

		/// <summary>
		/// Container that owns <see cref="StreamPtr"/>: the main input, or the separate
		/// audio input when the audio flux has its own URL. Null until a stream is opened.
		/// </summary>
		public AVFormatContext* Context { get; set; }

		/// <summary>
		/// Streams of this handler's media type available in <see cref="Context"/>, and the
		/// one currently decoded.
		/// </summary>
		public ITrack Track
			=> _track ??= new Track(this);

		/// <summary>Refresh the track list and raise its events (called every frame).</summary>
		internal void PollTrack()
			=> (_track ??= new Track(this)).Poll();

		private Track _track;

		// ── FFmpeg demux/decode state (owned by this handler) ────────────
		public PacketQueue Packets { get; protected set; } = new();
		public FrameQueue  Frames  { get; protected set; } = null;

		/// True when the stream has been decoded to the end and drained.
		public abstract bool HasEnded { get; }

		/// True when the demux queue for this stream has enough packets (not starving).
		public abstract bool HasEnoughPackets { get; }

		public Clock Clock { get; protected set; }
		public Decoder Decoder { get; protected set; }

		public abstract void Start();
		public abstract void Stop();

		public abstract void Open(int index, AVFormatContext* ic, AVCodecContext* avctx);
		public abstract void Close();

        internal virtual void OnPause(bool paused) {
			if (Clock == null) return;
			Clock.Paused = paused;
        }

		public virtual void OnSignalQuit() { }
    }
}
