using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Helpers;
using Nox.FFmpeg.Sources;
using UnityEngine;

namespace Nox.FFmpeg {
	public class MediaData {
		public StreamDecoder Decoder;
		public AVRational TimeBase;
		public double TimeBaseSeconds;
		public long Pts;
		public double StartTime;
		public AVPacket Packet;
		public AVFrame Frame;
		public readonly List<AVPacket> Cache = new();
	}

	public class Reader : IDisposable {

		public readonly BaseSource Context;
		public readonly bool IsValid;

		public const int CacheSize = 4096;
		public readonly Dictionary<AVMediaType, MediaData> Medias;

		// Propagate buffering/stalled events
		public event Action OnBufferingStart;
		public event Action OnBufferingEnd;
		public event Action OnStalledStart;
		public event Action OnStalledEnd;

		public Reader(BaseSource source, AVMediaType[] medias, AVHWDeviceType deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
			Context = source;
			IsValid = medias.Any(mediaType => Context.HasStream(mediaType));
			Medias  = new Dictionary<AVMediaType, MediaData>();
			foreach (var media in medias)
				Init(media, deviceType);

			// Connect buffering/stalled events
			if (Context != null) {
				Context.OnBufferingStart += () => OnBufferingStart?.Invoke();
				Context.OnBufferingEnd   += () => OnBufferingEnd?.Invoke();
				Context.OnStalledStart   += () => OnStalledStart?.Invoke();
				Context.OnStalledEnd     += () => OnStalledEnd?.Invoke();
			}
		}

		private void Init(AVMediaType type, AVHWDeviceType deviceType) {
			if (!IsValid)
				return;

			try {
				if (!Context.TryGetTimeBase(type, out var timeBase))
					throw new Exception("Failed to get time base");

				var      timeBaseSeconds = ffmpeg.av_q2d(timeBase);
				var      decoder         = new StreamDecoder(Context, type, deviceType);
				var      startTime       = 0d;
				AVPacket packet          = default;
				AVFrame  frame           = default;

				Debug.Log($"timeBase={timeBase.num}/{timeBase.den}");
				Debug.Log($"timeBaseSeconds={timeBaseSeconds}");

				// find the start time/time offset
				while (true) {
					if (!Context.NextFrame(out var p))
						break;

					if (decoder.Decode(out var f) != 0)
						continue;

					packet    = p;
					frame     = f;
					startTime = packet.dts * timeBaseSeconds;

					break;
				}

				Medias[type] = new MediaData() {
					Decoder         = decoder,
					TimeBase        = timeBase,
					TimeBaseSeconds = timeBaseSeconds,
					Pts             = packet.dts,
					StartTime       = startTime,
					Packet          = packet,
					Frame           = frame
				};
			} catch (Exception e) {
				Debug.LogError(new Exception($"Failed to initialize media type {type}", e));
			}
		}


		public void Update(AVMediaType type, double timestamp) {
			if (!IsValid)
				return;

			Medias[type].Pts = (long)(Math.Max(double.Epsilon, timestamp) / Medias[type].TimeBaseSeconds);
		}

		public void Seek(AVMediaType type, double timestamp) {
			if (!IsValid)
				return;

			var mediaData = Medias[type];

			Context.Seek(mediaData.Decoder, timestamp);
			mediaData.Decoder.Seek();
			Update(type, timestamp);

			mediaData.Packet = default;
			mediaData.Frame  = default;

			while (TryPopCache(type, out var p))
				;
		}

		public double Length
			=> IsValid
				? Context.GetLength(Medias.FirstOrDefault().Value.Decoder)
				: 0d;

		public bool IsEnded
			=> IsValid && Context.IsEnded;


		public bool TryGetFrame(AVMediaType type, int maxFrames, out AVFrame frame) {
			if (!IsValid) {
				frame = default;
				return false;
			}

			var i         = 0;
			var mediaData = Medias[type];
			var found     = false;

			while ((mediaData.Pts >= mediaData.Packet.dts || mediaData.Packet.dts == ffmpeg.AV_NOPTS_VALUE) && i <= maxFrames) {
				i++;

				if (!TryPopCache(type, out var p) && !Context.NextFrame(out p))
					break;

				var m = Context.GetMediaType(p);
				if (m != type) {
					Debug.LogWarning($"Expected media type {type} but got {m}");
					if (Medias.ContainsKey(m))
						AddCache(type, p);
					continue;
				}

				if (mediaData.Decoder.Decode(out var f) != 0)
					continue;

				mediaData.Packet = p;
				mediaData.Frame  = f;
				found            = true;
			}

			if (!found) {
				frame = default;
				return false;
			}

			frame = mediaData.Frame;
			return true;
		}

		private readonly AVFrame empty = default;

		private void AddCache(AVMediaType type, AVPacket p) {
			var cache = Medias[type].Cache;
			cache.Add(p);
			if (cache.Count > CacheSize)
				cache.RemoveAt(0);
		}

		private bool TryPopCache(AVMediaType type, out AVPacket pck) {
			var cache = Medias[type].Cache;
			for (var i = 0; i < cache.Count; i++) {
				var p = cache[i];
				pck = p;
				cache.RemoveAt(i);
				Debug.Log($"Popped packet from cache for media type {type}");
				return true;
			}

			pck = default;
			return false;
		}


		public int GetFrames(AVMediaType type, long maxDelta, ref AVFrame[] frames) {
			if (!IsValid)
				return 0;

			int i = 0,
				j = 0;

			var mediaData = Medias[type];
			var dts       = mediaData.Packet.dts;
			var maxFrames = frames.Length;

			while ((mediaData.Pts >= dts || mediaData.Packet.dts == ffmpeg.AV_NOPTS_VALUE) && i <= maxFrames) {
				var f = frames[j];
				i++;

				if (!TryPopCache(type, out var p) && !Context.NextFrame(out p))
					break;

				var m = Context.GetMediaType(p);
				if (m != type) {
					Debug.LogWarning($"Expected media type {type} but got {m}");
					if (Medias.ContainsKey(m))
						AddCache(type, p);
					continue;
				}

				if (mediaData.Decoder.DecodeNonAlloc(ref f) != 0)
					continue;

				mediaData.Packet = p;
				mediaData.Frame  = f;

				if (Math.Abs(mediaData.Pts - p.dts) > maxDelta)
					continue;

				dts         = mediaData.Packet.dts;
				frames[j++] = f;
			}

			var capturedFrames = j;
			while (j < frames.Length)
				frames[j++] = empty;

			return capturedFrames;
		}

		public void Dispose() {
			foreach (var media in Medias.Values) {
				media.Cache.Clear();
				media.Decoder?.Dispose();
			}
			Medias.Clear();
			Context?.Dispose();
		}
	}
}