using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;
using UnityEngine;

namespace Nox.FFmpeg.Helpers {
	public static class Initializer {
		private static bool _initialized = false;
		private const int log_level = ffmpeg.AV_LOG_VERBOSE;

		private static readonly string[] _possiblePaths = {
			#if UNITY_EDITOR_WIN
			Path.Combine(Application.dataPath, "..", "Packages/nox.ffmpeg/Plugins"),
			#endif
			Path.Combine(Application.dataPath, "Plugins"),
			Path.Combine(Application.dataPath, "Plugins/x86_64"),
			"Assets/FFmpeg",
			"Assets/Plugins"
		};

		private static string FindFolder() {
			var map      = ffmpeg.LibraryVersionMap;
			var resolver = DynamicallyLoadedBindings.FunctionResolver is FunctionResolverBase rb ? rb : null;
			var method = resolver?.GetType().GetMethod("GetNativeLibraryName",
				System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.Instance);

			foreach (var path in _possiblePaths)
				if (Directory.Exists(path)) {
					if (resolver == null)
						return path;

					// get method info for GetNativeLibraryName
					if (method == null) {
						Debug.LogWarning($"Could not find method GetNativeLibraryName in {resolver.GetType().Name}. Skipping library name checks.");
						var methods = resolver.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
						Debug.LogWarning($"Available methods in {resolver.GetType().Name}: {string.Join(", ", methods.Select(m => m.Name))}");
						return path;
					}

					// Check if the required FFmpeg libraries are present
					var allPresent = true;
					foreach (var (lib, ver) in map) {
						var name = method.Invoke(resolver, new object[] { lib, ver }) as string;
						if (string.IsNullOrEmpty(name)) {
							Debug.LogWarning($"Could not get native library name for {lib}. Skipping check for this library.");
							continue;
						}

						var libPath = Path.Combine(path, name);
						if (File.Exists(libPath))
							continue;

						Debug.LogWarning($"Library {name} not found in {path}. FFmpeg may not work properly.");
						allPresent = false;
					}

					if (allPresent)
						return path;
				}
			throw new Exception("FFmpeg folder not found. Please ensure FFmpeg is properly installed and the folder is in one of the following locations: " + string.Join(", ", _possiblePaths));
		}

		public static void Initialize() {
			if (_initialized)
				return;
			_initialized = true;

			ffmpeg.RootPath = FindFolder();
			DynamicallyLoadedBindings.Initialize();
		}
	}
}