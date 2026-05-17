using System;
using System.Collections.Generic;
using System.Threading;
using FFmpeg.AutoGen;

namespace Nox.FFmpeg.Utils {
	public unsafe class PacketQueue : IDisposable {
		private readonly Queue<PacketList> _list = new();
		public int NbPackets { get; private set; }
		public int Size { get; private set; } // bytes
		public long Duration { get; private set; }
		public int Serial { get; private set; }
		public bool AbortRequest { get; private set; } = true;

		private readonly object _lock = new();
		private readonly SemaphoreSlim _cond = new(0);

		// packet_queue_init: constructor; abort_request starts true, serial starts 0
		public PacketQueue() { }

		public int GetSerial()
			=> Serial;

		// packet_queue_start
		public void Start() {
			lock (_lock) {
				AbortRequest = false;
				Serial++;
			}
			_cond.Release();
		}

		// packet_queue_abort
		public void Abort() {
			lock (_lock)
				AbortRequest = true;
			_cond.Release();
		}

		// packet_queue_flush
		public void Flush() {
			lock (_lock) {
				while (_list.Count > 0) {
					var item = _list.Dequeue();
					ffmpeg.av_packet_free(&item.Pkt);
				}
				NbPackets = 0;
				Size      = 0;
				Duration  = 0;
				Serial++;
			}
		}

		// packet_queue_put_private
		private int PutPrivate(AVPacket* pkt) {
			if (AbortRequest)
				return -1;
			var item = new PacketList { Pkt = pkt, Serial = Serial };
			_list.Enqueue(item);
			NbPackets++;
			Size     += pkt->size + sizeof(PacketList);
			Duration += pkt->duration;
			_cond.Release();
			return 0;
		}

		// packet_queue_put
		public int Put(AVPacket* pkt) {
			AVPacket* pkt1 = ffmpeg.av_packet_alloc();
			if (pkt1 == null) {
				ffmpeg.av_packet_unref(pkt);
				return -1;
			}
			ffmpeg.av_packet_move_ref(pkt1, pkt);
			lock (_lock) {
				int ret = PutPrivate(pkt1);
				if (ret < 0)
					ffmpeg.av_packet_free(&pkt1);
				return ret;
			}
		}

		// packet_queue_put_nullpacket
		public int PutNullPacket(AVPacket* pkt, int streamIndex) {
			pkt->stream_index = streamIndex;
			return Put(pkt);
		}

		// packet_queue_get — block=true blocks until available or aborted
		// returns < 0 if aborted, 0 if no packet (non-blocking), 1 if packet
		public int Get(AVPacket* pkt, bool block, out int serial) {
			serial = -1;
			while (true) {
				lock (_lock) {
					if (AbortRequest)
						return -1;
					if (_list.Count > 0) {
						var item = _list.Dequeue();
						NbPackets--;
						Size     -= item.Pkt->size + sizeof(PacketList);
						Duration -= item.Pkt->duration;
						ffmpeg.av_packet_move_ref(pkt, item.Pkt);
						serial = item.Serial;
						ffmpeg.av_packet_free(&item.Pkt);
						return 1;
					}
					if (!block)
						return 0;
				}
				_cond.Wait();
			}
		}

		public void Dispose() {
			Flush();
			_cond.Dispose();
		}
	}
}