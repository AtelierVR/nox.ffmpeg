using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using UnityEngine.Events;
using IHandler = Nox.FFmpeg.Base.IHandler;
using ITrack = Nox.VideoPlayer.ITrack;
using IVideoPlayer = Nox.VideoPlayer.IVideoPlayer;
using LogType = Nox.CCK.Utils.LogType;

namespace Nox.FFmpeg.Base {
	/// <summary>
	/// Streams of one <see cref="IHandler"/>'s media type available in its container
	/// (<see cref="IHandler.Context"/>), and the one currently decoded by that handler.
	/// Switching a track mirrors ffplay's <c>stream_cycle_channel()</c>:
	/// <c>stream_component_close()</c> followed by <c>stream_component_open()</c>.
	/// </summary>
	public unsafe class Track : ITrack {
		private readonly IHandler _handler;

		private readonly List<int>    _indices = new();
		private readonly List<string> _names   = new();

		// Container the lists were built from — the read thread sets it (and the
		// stream indices) asynchronously, hence the polling.
		private AVFormatContext* _context;
		private uint             _streamCount;
		private int              _lastSelected = -1;

		public Track(IHandler handler) 
			=> _handler = handler;

		#region ITrack

		/// <summary>
		/// Index (in <see cref="Names"/>) of the track being decoded, or -1. Set it to
		/// decode another track: the index is a position in <see cref="Names"/>, not an
		/// FFmpeg stream index.
		/// </summary>
		public int Selected {
			get => _indices.IndexOf(_handler.StreamIndex);
			set => Select(value);
		}

		/// <summary>Display name of every track of this type, in container order.</summary>
		public IReadOnlyList<string> Names
			=> _names;

		/// <summary>Event invoked when the decoded track changes.</summary>
		public UnityEvent<IVideoPlayer, int> OnSelected { get; } = new();

		/// <summary>Event invoked when the track list changes (new container).</summary>
		public UnityEvent<IVideoPlayer, IReadOnlyList<string>> OnChanged { get; } = new();

		#endregion ITrack

		#region Container

		private PlayerState State
			=> _handler.State;

		private IVideoPlayer Owner
			=> State?.Player;

		/// <summary>
		/// Rebuild the track list when its container changed (or the streams it
		/// announces have been resolved), then raise <see cref="OnSelected"/> when the
		/// decoded stream changed. Called every frame by <see cref="PlayerState"/>.
		/// </summary>
		internal void Poll() {
			Refresh();

			int selected = Selected;
			if (selected == _lastSelected)
				return;

			_lastSelected = selected;
			OnSelected.Invoke(Owner, selected);
		}

		private void Refresh() {
			var context = _handler.Context;
			if (context == _context && (context == null || context->nb_streams == _streamCount))
				return;

			// The container is filled by the read thread: wait until it has opened a
			// stream (i.e. avformat_find_stream_info() returned) before walking the
			// stream array, which it reallocates.
			if (context != null && _handler.StreamPtr == null)
				return;

			_context     = context;
			_streamCount = context == null ? 0 : context->nb_streams;

			var indices = new List<int>();
			var names   = new List<string>();
			if (context != null)
				for (uint i = 0; i < context->nb_streams; i++) {
					var stream = context->streams[i];
					if (stream == null || stream->codecpar == null || !Matches(stream->codecpar))
						continue;
					indices.Add((int)i);
					names.Add(Describe(stream));
				}

			if (indices.Count == _indices.Count) {
				bool identical = true;
				for (int i = 0; i < indices.Count && identical; i++)
					identical = indices[i] == _indices[i] && names[i] == _names[i];
				if (identical)
					return;
			}

			_indices.Clear();
			_indices.AddRange(indices);
			_names.Clear();
			_names.AddRange(names);
			OnChanged.Invoke(Owner, _names.ToArray());
		}

		/// <summary>Whether those codec parameters belong to one of the handler's media types.</summary>
		private bool Matches(AVCodecParameters* parameters)
			=> Array.Exists(_handler.MediaTypes, type => type == parameters->codec_type);

		/// <summary>Human readable name of a stream: title, language, codec and format.</summary>
		private static string Describe(AVStream* stream) {
			var par  = stream->codecpar;
			var info = new List<string>(3);

			var title    = Metadata(stream->metadata, "title");
			var language = Metadata(stream->metadata, "language");

			if (!string.IsNullOrWhiteSpace(title))
				info.Add(title.Trim());
			if (!string.IsNullOrWhiteSpace(language))
				info.Add(language.Trim().ToUpperInvariant());
			if (info.Count == 0)
				info.Add($"#{stream->index}");

			string codec  = ffmpeg.avcodec_get_name(par->codec_id);
			string format = par->width > 0 && par->height > 0
				? $"{par->width}x{par->height}"
				: par->ch_layout.nb_channels > 0
					? $"{par->ch_layout.nb_channels}ch"
					: null;
			info.Add(format == null ? codec : $"{codec} {format}");

			return string.Join(" · ", info);
		}

		private static string Metadata(AVDictionary* dictionary, string key) {
			var entry = ffmpeg.av_dict_get(dictionary, key, null, ffmpeg.AV_DICT_IGNORE_SUFFIX);
			return entry == null || entry->value == null
				? null
				: Marshal.PtrToStringAnsi((IntPtr)entry->value);
		}

		/// <summary>
		/// Decode the track at <paramref name="index"/> instead of the current one.
		/// The index is a position in <see cref="Names"/>, not an FFmpeg stream index.
		/// </summary>
		private void Select(int index) {
			Poll(); // the list is filled by the read thread: make sure it is up to date

			var state   = State;
			var context = _handler.Context;
			if (state == null || context == null) {
				Log(LogType.Warning, $"Cannot select {_handler.Type} track {index}: the player is not ready yet.");
				return;
			}
			if (index < 0 || index >= _indices.Count) {
				Log(LogType.Warning,
					$"Cannot select {_handler.Type} track {index}: {_indices.Count} track(s) available.");
				return;
			}

			int stream = _indices[index];
			if (_handler.StreamIndex == stream) {
				Poll();
				return;
			}

			// stream_component_close(): aborts the decoder thread and flushes its
			// packets. Queued frames are dropped too, so the old track is not
			// displayed/played once the new one is opened.
			if (_handler.StreamIndex >= 0)
				state.CloseStream(_handler.StreamIndex, context);
			_handler.Frames?.Flush();

			// stream_component_open(): the demuxer feeds this stream from now on.
			if (state.OpenStream(stream, context) < 0) {
				Log(LogType.Error, $"Failed to open {_handler.Type} stream #{stream}.");
				Poll();
				return;
			}

			Log(LogType.Log, $"Selected {_handler.Type} track {index} ({_names[index]}).");
			Poll(); // raise OnSelected with the new index
		}

		private void Log(LogType type, string message)
			=> State?.OnMessage.Invoke(type, message);

		#endregion Container
	}
}
