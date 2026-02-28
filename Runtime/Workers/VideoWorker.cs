using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Workers {
	public class VideoWorker : BaseWorker {
		public override AVMediaType MediaType
			=> AVMediaType.AVMEDIA_TYPE_VIDEO;

		override protected void ThreadUpdate() {
			while (!Context.IsPaused) {
				Thread.Yield();

				try {
					if (timings == null)
						continue;

					timings.Update(MediaType, Time);
					if (timings.TryGetFrame(MediaType, 250, out var frame))
						PlayPacket(frame);
				} catch (Exception e) {
					Debug.LogException(e);
					break;
				}
			}
		}

		#region FFTexturePlayer Helpers

		public readonly UnityEvent<Texture2D> OnDisplay = new();
		public readonly UnityEvent<Vector2Int> OnResize = new();

		public Texture2D Texture;
		public Vector2Int Dimensions = Vector2Int.one;

		private int frameWidth;
		private int frameHeight;
		private long framePts;
		private byte[] frameData = Array.Empty<byte>();
		private byte[] backBuffer = Array.Empty<byte>();

		private readonly Mutex mutex = new Mutex();

		[Header("Video Settings")]
		[Tooltip("Force output texture size to specified width. Set to 0 to use source width.")]
		public int imageWidth = 1280;

		[Tooltip("Force output texture size to specified height. Set to 0 to use source height.")]
		public int imageHeight = 720;

		[Tooltip("Flags whether the texture data should be flipped on the Y axis or not. Minor performance cost when enabled.")]
		public bool flipTexture = true;

		private NativeArray<byte> texData;

		public void PlayPacket(AVFrame frame) {
			int        width      = imageWidth > 0 ? imageWidth : frame.width;
			int        height     = imageHeight > 0 ? imageHeight : frame.height;
			const byte pixelWidth = 3;
			int        len        = width * height * pixelWidth;
			if (backBuffer.Length != len)
				backBuffer = new byte[ len ];

			if (!SaveFrame(frame, backBuffer, width, height))
				return;

			if (!mutex.WaitOne())
				return;

			try {
				framePts    = frame.pts;
				frameWidth  = width;
				frameHeight = height;
				if (frameData.Length != len)
					frameData = new byte[ len ];

				if (flipTexture)
					CopyAndFlip(backBuffer, frameData, frameWidth, frameHeight, pixelWidth);
				else
					Array.Copy(backBuffer, frameData, len);
			} finally {
				mutex.ReleaseMutex();
			}
		}

		private void Update() {
			if (framePts != pts && mutex.WaitOne(0)) {
				try {
					pts = framePts;

					// Update video clock with PTS
					if (timings?.IsValid == true) {
						double ptsSeconds = pts * timings.Medias[MediaType].TimeBaseSeconds;
						UpdateClock(ptsSeconds, serial);

						// Sync external clock to video clock if video is master
						if (Context.SyncWorkerIndex == Index)
							Context.clock.SyncToSlave(clock);
					}

					if (Dimensions.x != frameWidth || Dimensions.y != frameHeight) {
						Dimensions = new Vector2Int(frameWidth, frameHeight);
						OnResize.Invoke(Dimensions);
					}

					DisplayBytes(frameData, frameWidth, frameHeight);
					OnDisplay.Invoke(Texture);
				} catch (Exception e) {
					Debug.LogException(new Exception("Failed to display video frame", e));
				} finally {
					mutex.ReleaseMutex();
				}
			}
		}

		private void DisplayBytes(byte[] data, int width, int height) {
			if (data == null || data.Length == 0)
				return;

			if (!Texture)
				Texture = new Texture2D(16, 16, TextureFormat.RGB24, false) {
					name = "FFmpeg Video Texture"
				};

			if (Texture.width != width || Texture.height != height) {
				// image presumed to be changed. Cache new image information.
				Texture.Reinitialize(width, height);
				texData = Texture.GetRawTextureData<byte>();
			}

			texData.CopyFrom(data);
			Texture.Apply(false);
		}

		#region Utils

		[ThreadStatic] private static byte[] line;

		public static unsafe bool SaveFrame(AVFrame frame, byte[] texture, int width, int height) {
			line ??= new byte[ 4096 * 4096 * 6 ];

			if (frame.data[0] == null || frame.format == -1 || texture == null)
				return false;

			using var converter = new FrameConverter(
				new Size(frame.width, frame.height),
				(AVPixelFormat)frame.format,
				new Size(width, height),
				AVPixelFormat.AV_PIX_FMT_RGB24
			);

			var convFrame = converter.Convert(frame);
			var len       = convFrame.width * convFrame.height * 3;

			Marshal.Copy((IntPtr)convFrame.data[0], line, 0, len);
			Array.Copy(line, 0, texture, 0, len);
			return true;
		}

		public static unsafe void CopyAndFlip(byte[] src, byte[] dst, int width, int height, int pixelWidth) {
			int rowWidth      = width * pixelWidth;
			int heightLessOne = height - 1;

			fixed (byte* srcPtr = src)
			fixed (byte* dstPtr = dst) {
				// Copy rows in reverse order
				for (int y = 0; y < height; y++) {
					byte* srcRow = srcPtr + (y * rowWidth);
					byte* dstRow = dstPtr + ((heightLessOne - y) * rowWidth);
					UnsafeUtility.MemCpy(dstRow, srcRow, rowWidth);
				}
			}
		}

		#endregion

		#endregion
	}
}