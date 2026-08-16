using System;
using System.Threading;
namespace Nox.FFmpeg.Utils {
	public class FrameQueue: IDisposable {
			private readonly Frame[] _queue;
		private int _rindex,
			_windex,
			_size;
		private int _rindexShown;
		private readonly bool _keepLast;
		private readonly PacketQueue _pktq;
		private readonly object _lock = new();
		private readonly SemaphoreSlim _cond = new(0);

		public FrameQueue(PacketQueue pktq, int maxSize, bool keepLast) {
			_pktq     = pktq;
			_keepLast = keepLast;
			int cap = Math.Min(maxSize, Constants.FRAME_QUEUE_SIZE);
			_queue = new Frame[ cap ];
			for (int i = 0; i < cap; i++)
				_queue[i] = new Frame();
		}

		public int NbRemaining()
			=> _size - _rindexShown;

		// frame_queue_peek_writable — blocks until space available
		public Frame PeekWritable() {
			while (true) {
				lock (_lock) {
					if (_pktq.AbortRequest)
						return null;
					if (_size < _queue.Length)
						return _queue[_windex];
				}
				_cond.Wait();
			}
		}

		// frame_queue_peek_readable — blocks until a frame is available
		public Frame PeekReadable() {
			while (true) {
				lock (_lock) {
					if (_pktq.AbortRequest)
						return null;
					if (_size - _rindexShown > 0)
						return _queue[(_rindex + _rindexShown) % _queue.Length];
				}
				_cond.Wait();
			}
		}

		// frame_queue_peek
		public Frame Peek()
			=> _queue[(_rindex + _rindexShown) % _queue.Length];
		// frame_queue_peek_next
		public Frame PeekNext()
			=> _queue[(_rindex + _rindexShown + 1) % _queue.Length];
		// frame_queue_peek_last
		public Frame PeekLast()
			=> _queue[_rindex];

		// frame_queue_push
		public void Push() {
			lock (_lock) {
				if (++_windex == _queue.Length)
					_windex = 0;
				_size++;
				_cond.Release();
			}
		}

		// frame_queue_next
		public void Next() {
			if (_keepLast && _rindexShown == 0) {
				_rindexShown = 1;
				return;
			}
			_queue[_rindex].Unref();
			lock (_lock) {
				if (++_rindex == _queue.Length)
					_rindex = 0;
				_size--;
				_cond.Release();
			}
		}

		// frame_queue_last_pos
		public long LastPos() {
			var fp = _queue[_rindex];
			if (_rindexShown != 0 && fp.Serial == _pktq.Serial)
				return fp.Pos;
			return -1;
		}

		public void Signal() {
			lock (_lock)
				_cond.Release();
		}

		public void Dispose() {
			foreach (var f in _queue)
				f.Free();
			_cond.Dispose();
		}
	}
}