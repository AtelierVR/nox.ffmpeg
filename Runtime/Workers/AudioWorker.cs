using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Workers {
	public class AudioWorker : BaseWorker {
		private AVFrame[] frames = new AVFrame[ 500 ];

		public override AVMediaType MediaType
			=> AVMediaType.AVMEDIA_TYPE_AUDIO;

		public override void Init() {
			if (timings?.IsValid == true) {
				var media = timings.Medias[MediaType];
				Init(media.Decoder.SampleRate, media.Decoder.Channels, media.Decoder.SampleFormat);
			}
			base.Init();
		}

		override protected void ThreadUpdate() {
			while (!Context.IsPaused) {
				Thread.Yield();

				try {
					if (timings == null)
						continue;

					timings.Update(MediaType, Time);
					var frameCount = timings.GetFrames(MediaType, 250, ref frames);
					PlayPackets(frames, frameCount);
				} catch (Exception e) {
					Debug.LogException(e);
					break;
				}
			}
		}

		#region FFAudioPlayer Helpers

		public float Volume {
			get => volume;
			set {
				volume = value;
				OnVolumeChange?.Invoke(value);
			}
		}

		private float volume = -1f;

		public UnityEvent<float> OnVolumeChange = new();

		public delegate void AddQueueDelegate(ICollection<float> pcm, int channels, int frequency);

		public event AddQueueDelegate AddQueue;

		[Header("Audio Settings")]
		public float bufferSize = 2f;
		private int channels;
		private int frequency;
		private AVSampleFormat sampleFormat;
		private readonly List<float> pcm = new List<float>();

		public void Init(int frequency, int channels, AVSampleFormat sampleFormat) {
			this.channels     = channels;
			this.sampleFormat = sampleFormat;
			this.frequency    = frequency;
			Debug.Log($"Freq={frequency} channels={channels}");
		}

		public void SetVolume(float volume)
			=> OnVolumeChange?.Invoke(volume);


		public void PlayPackets(ICollection<AVFrame> frames, int frameCount = -1) {
			if (frames.Count == 0)
				return;
			if (frameCount == -1)
				frameCount = frames.Count;

			foreach (var frame in frames) {
				if (frameCount == 0)
					break;
				frameCount--;
				QueuePacket(frame);
			}
		}

		private unsafe void QueuePacket(AVFrame frame) {
			// TODO: multi-channel output support
			pcm.Clear();
			pts = frame.pts;
			int size = ffmpeg.av_samples_get_buffer_size(null, 1, frame.nb_samples, sampleFormat, 1);
			if (size < 0) {
				Debug.LogError("audio buffer size is less than zero");
				return;
			}

			for (int i = 0; i < size / sizeof(float); i++)
				pcm.Add(0f);
			for (uint ch = 0; ch < channels; ch++) {
				byte[]  backBuffer2 = new byte[ size ];
				float[] backBuffer3 = new float[ size / sizeof(float) ];
				Marshal.Copy((IntPtr)frame.data[ch], backBuffer2, 0, size);
				Buffer.BlockCopy(backBuffer2, 0, backBuffer3, 0, backBuffer2.Length);
				for (int i = 0; i < backBuffer3.Length; i++) {
					if (ch == 0)
						pcm[i] = backBuffer3[i];
					else
						pcm[i] = (pcm[i] + backBuffer3[i]) / 2f;
					// pcm.Add(backBuffer3[i]);
				}

				// break;
			}

			AddQueue?.Invoke(pcm, 1, frequency);
		}

		#endregion
	}
}