using System;
using FFmpeg.AutoGen;
using Nox.FFmpeg.Executor;
using UnityEngine;

namespace Nox.FFmpeg.Helpers {
	public static class Initializer {
		private static bool _initialized = false;
		private const int log_level = ffmpeg.AV_LOG_VERBOSE;

		/// <summary>
		/// Resolves the native module name (e.g. "avcodec-61.dll") for each library in
		/// <see cref="ffmpeg.LibraryVersionMap"/> via the active FFmpeg function resolver, then
		/// loads it through <see cref="Main.CoreAPI"/>'s LibAPI. The LibAPI handles mod-aware
		/// plugin folders, platform detection and reference-counted loading.
		/// </summary>
		private static void LoadLibraries() {
			var libAPI = Main.CoreAPI?.LibAPI;
			if (libAPI == null) {
				Debug.LogWarning("[FFmpeg] LibAPI is not available; native libraries will not be pre-loaded.");
				return;
			}

			// Resolve the function resolver (the same instance DynamicallyLoadedBindings will use).
			var resolver = DynamicallyLoadedBindings.FunctionResolver as FunctionResolverBase
				?? FunctionResolverFactory.Create();

			var method = resolver.GetType().GetMethod("GetNativeLibraryName",
				System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.Instance);

			if (method == null) {
				Debug.LogWarning($"[FFmpeg] Could not resolve GetNativeLibraryName on {resolver.GetType().Name}.");
				return;
			}

			var extension = libAPI.GetExtension();

			foreach (var (lib, ver) in ffmpeg.LibraryVersionMap) {
				var nativeName = method.Invoke(resolver, new object[] { lib, ver }) as string;
				if (string.IsNullOrEmpty(nativeName))
					continue;

				// LibAPI.Load expects a name without the platform extension (e.g. "avcodec-61").
				var name = nativeName;
				if (!string.IsNullOrEmpty(extension) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
					name = name.Substring(0, name.Length - extension.Length);

				try {
					libAPI.Load(name);
				} catch (DllNotFoundException) {
					Debug.LogWarning($"[FFmpeg] Native library '{nativeName}' not found via LibAPI. FFmpeg may not work properly.");
				}
			}
		}

		public static void Initialize() {
			if (_initialized)
				return;
			_initialized = true;

			// Point FFmpeg.AutoGen at the mod-aware plugin folders and pre-load the libraries
			// through LibAPI so resolution of the versioned native modules succeeds.
			var folders = Main.CoreAPI?.LibAPI?.GetFolders();
			if (folders != null && folders.Length > 0)
				ffmpeg.RootPath = folders[0];

			LoadLibraries();
			DynamicallyLoadedBindings.Initialize();
		}
	}
}