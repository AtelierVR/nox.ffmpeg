using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Utils;
using Nox.FFmpeg.Base;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Handlers {
	/// Decodes video frames and converts them to a <see cref="Texture2D"/>.
	public unsafe sealed class VideoHandler : IHandler {
		public override StreamType Type 
			=> StreamType.Video;

		public Texture2D Frame { get; private set; }
		public UnityEvent<Texture2D> OnFrame { get; } = new();

		public Vector2Int Resolution { get; private set; } = Vector2Int.zero;
		public UnityEvent<Vector2Int> OnResolution { get; } = new();

		private readonly Player player;

		private Converter _converter;
		private int _convW, _convH;
		private AVPixelFormat _convFormat;
		private byte[] _rgb;

		public VideoHandler(Player p) {
			player = p;
			OnFrame.AddListener(frame => player.OnTexture.Invoke(player, frame));
		}

		public override void Start() {
			IsRunning = true;
			player.State.OnVideoFrameReady = HandleFrame;
		}

		public override void Stop() {
			IsRunning = false;
			player.State.OnVideoFrameReady = null;
			_converter?.Dispose();
			_converter = null;
			if (Frame) {
				UnityEngine.Object.Destroy(Frame);
				Frame = null;
			}
		}

		private void HandleFrame(IntPtr framePtr) {
			AVFrame* frame = (AVFrame*)framePtr;
			if (frame == null || frame->data[0] == null || frame->format == -1)
				return;

			int w = frame->width,
				h = frame->height;
			int len = w * h * 3;

			// Recreate the converter only when the source size/format changes.
			var fmt = (AVPixelFormat)frame->format;
			if (_converter == null || _convW != w || _convH != h || _convFormat != fmt) {
				_converter?.Dispose();
				_converter = new Converter(
					new System.Drawing.Size(w, h), fmt,
					new System.Drawing.Size(w, h), AVPixelFormat.AV_PIX_FMT_RGB24);
				_convW      = w;
				_convH      = h;
				_convFormat = fmt;
			}

			if (_rgb == null || _rgb.Length != len)
				_rgb = new byte[len];

			var converted = _converter.Convert(*frame);
			Marshal.Copy((IntPtr)converted.data[0], _rgb, 0, len);

			Upload(w, h, _rgb);
		}

		private void Upload(int w, int h, byte[] buf) {
			if (!Frame || Frame.width != w || Frame.height != h) {
				if (Frame)
					UnityEngine.Object.Destroy(Frame);
				Frame = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "FFplay" };
				var res = new Vector2Int(w, h);
				if (Resolution != res) {
					Resolution = res;
					OnResolution.Invoke(res);
				}
			}
			Frame.LoadRawTextureData(buf);
			Frame.Apply(false);
			OnFrame.Invoke(Frame);
		}
	}
}