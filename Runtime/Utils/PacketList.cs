using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Utils {

	// ─────────────────────────────────────────────────────────────────────────
	// MyAVPacketList / PacketQueue — mirrors ffplay.c PacketQueue
	// ─────────────────────────────────────────────────────────────────────────
	public struct PacketList {
		public unsafe AVPacket* Pkt;
		public int Serial;
	}
}