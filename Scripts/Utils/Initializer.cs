using System;
using System.Collections.Generic;
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

			// Load in dependency order (leaves first) so `dlopen(..., RTLD_NOW)`
			// can resolve cross-library symbols between the versioned FFmpeg modules.
			foreach (var (lib, ver) in DependencyOrder(ffmpeg.LibraryVersionMap.Keys)) {
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

		/// <summary>
		/// Orders FFmpeg libraries so that dependencies are loaded before their dependents
		/// (leaves of <see cref="FunctionResolverBase.LibraryDependenciesMap"/> come first).
		/// </summary>
		private static IEnumerable<(string lib, int ver)> DependencyOrder(IEnumerable<string> libs) {
			var map = FunctionResolverBase.LibraryDependenciesMap;
			var resolved = new HashSet<string>();
			var visiting = new HashSet<string>();
			var list = new List<string>();

			void Visit(string lib) {
				if (resolved.Contains(lib)) return;
				if (!visiting.Add(lib)) return; // cycle guard
				foreach (var dep in map.TryGetValue(lib, out var deps) ? deps : Array.Empty<string>())
					Visit(dep);
				visiting.Remove(lib);
				resolved.Add(lib);
				list.Add(lib);
			}

			foreach (var lib in libs)
				Visit(lib);

			foreach (var lib in list)
				if (ffmpeg.LibraryVersionMap.TryGetValue(lib, out var ver))
					yield return (lib, ver);
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

			// Register network protocols (http, https, tls, …). Without this,
			// avformat_open_input on a URL fails with "Protocol not found".
			ffmpeg.avformat_network_init();
		}
	}
}