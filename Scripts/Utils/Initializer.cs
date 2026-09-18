using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Nox.CCK.Mods.Libs;
using Nox.CCK.Utils;
using Nox.FFmpeg.Executor;

namespace Nox.FFmpeg.Helpers {
	public static class Initializer {
		private static bool _initialized = false;
		private const int log_level = ffmpeg.AV_LOG_VERBOSE;
		private const string LogTag = "FFmpeg";

		/// <summary>
		/// Resolves FFmpeg exports through the mod-aware native loader
		/// (<see cref="Main.CoreAPI"/>'s LibAPI) instead of FFmpeg.AutoGen's own loader.
		/// <para>
		/// FFmpeg.AutoGen's default resolver builds its own path
		/// (<c>Path.Combine(ffmpeg.RootPath, "avutil-59.so")</c>) and <c>dlopen</c>s it with
		/// <c>RTLD_NOW</c>, bypassing the mod-aware search folders and the reference-counted
		/// handles already opened by the LibAPI. When that load fails — different folder layout, or
		/// a missing transitive dependency such as <c>libcrypto.so.3</c> on the target machine —
		/// the resolver silently returns <c>null</c> (because
		/// <see cref="DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound"/> is <c>false</c>),
		/// the generated bindings then install <c>delegate { throw new NotSupportedException(); }</c>,
		/// and the real cause only shows up much later as a cryptic <c>NotSupportedException</c>
		/// thrown from <c>Clock</c>.
		/// </para>
		/// </summary>
		private sealed class LibApiFunctionResolver : IFunctionResolver {
			private readonly ILibAPI _libAPI;

			// Building these reads the current Unity build target (PlatformExtensions.CurrentPlatform,
			// editor-only API), so they are captured once, on the main thread: exports are resolved
			// lazily from the FFmpeg read thread, where calling them would throw
			// "get_activeBuildTarget can only be called from the main thread".
			private readonly string _extension;
			private readonly string _folders;

			public LibApiFunctionResolver(ILibAPI libAPI) {
				_libAPI    = libAPI;
				_extension = libAPI.GetExtension();
				_folders   = string.Join(", ", libAPI.GetFolders());
			}

			public T GetFunctionDelegate<T>(string libraryName, string functionName, bool throwOnError = true) {
				// ILibAPI expects a module name without extension (e.g. "avutil-59"): the loader
				// appends the platform extension itself.
				var moduleName = ModuleName(libraryName);
				var fileName = moduleName + _extension;
				string failure;

				try {
					// Reference-counted: a no-op when the library is already loaded.
					_libAPI.Load(moduleName);

					var pointer = _libAPI.GetSymbol(moduleName, functionName);
					if (pointer != IntPtr.Zero)
						return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));

					failure = $"'{fileName}' does not export '{functionName}'";
				} catch (Exception e) {
					failure = $"'{fileName}' could not be loaded ({e.Message})";
				}

				var message = $"Native symbol unreachable: {failure}. Searched: {_folders}.";

				if (throwOnError)
					throw new EntryPointNotFoundException(message);

				// The generated bindings turn a null result into
				// `delegate { throw new NotSupportedException(); }`, so report the real reason here.
				Logger.LogError(message, LogTag);
				return default;
			}
		}

		/// <summary>
		/// Module name of a logical FFmpeg library, following the
		/// <c>{library}-{version}</c> convention (e.g. "avutil-59") — the form expected by the
		/// LibAPI, which appends the platform extension itself.
		/// </summary>
		private static string ModuleName(string libraryName)
			=> ffmpeg.LibraryVersionMap.TryGetValue(libraryName, out var version)
				? $"{libraryName}-{version}"
				: libraryName;

		/// <summary>
		/// Pre-loads every library of <see cref="ffmpeg.LibraryVersionMap"/> through the LibAPI,
		/// which handles mod-aware plugin folders, platform detection and reference-counted loading.
		/// </summary>
		private static void LoadLibraries(ILibAPI libAPI) {
			// Load in dependency order (leaves first) so `dlopen(..., RTLD_NOW)`
			// can resolve cross-library symbols between the versioned FFmpeg modules.
			foreach (var lib in DependencyOrder(ffmpeg.LibraryVersionMap.Keys)) {
				try {
					libAPI.Load(ModuleName(lib));
				} catch (DllNotFoundException e) {
					Logger.LogWarning($"Native library '{ModuleName(lib)}{libAPI.GetExtension()}' could not be loaded; playback will fail. " +
						$"Searched: {string.Join(", ", libAPI.GetFolders())}. {e.Message}", LogTag);
				}
			}
		}

		/// <summary>
		/// Probes the native backend. <c>avutil/av_gettime_relative</c> is the first export the
		/// player touches (see <c>Clock</c>), so validating it here reports a broken install once,
		/// at startup, together with the searched folders — instead of failing inside the first
		/// playback attempt.
		/// </summary>
		private static void VerifyBackend(ILibAPI libAPI) {
			var moduleName = ModuleName("avutil");
			try {
				libAPI.Load(moduleName);
				if (libAPI.GetSymbol(moduleName, "av_gettime_relative") != IntPtr.Zero)
					return;

				Logger.LogError($"'{moduleName}{libAPI.GetExtension()}' does not export 'av_gettime_relative'; " +
					"the native FFmpeg backend cannot be used and video playback will fail.", LogTag);
			} catch (Exception e) {
				Logger.LogError($"The native FFmpeg backend is unusable ({e.Message}). " +
					$"Searched: {string.Join(", ", libAPI.GetFolders())}. Video playback will fail.", LogTag);
			}
		}

		/// <summary>
		/// Orders FFmpeg libraries so that dependencies are loaded before their dependents
		/// (leaves of <see cref="FunctionResolverBase.LibraryDependenciesMap"/> come first).
		/// </summary>
		private static IEnumerable<string> DependencyOrder(IEnumerable<string> libs) {
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

			return list;
		}

		public static void Initialize() {
			if (_initialized)
				return;

			var libAPI = Main.CoreAPI?.LibAPI;

			if (libAPI == null) {
				Logger.LogWarning("LibAPI is not available; native libraries will not be pre-loaded.", LogTag);
			} else {
				// Resolve every FFmpeg export through the mod-aware loader, which already knows
				// where the native modules live and can report why a load failed.
				DynamicallyLoadedBindings.FunctionResolver = new LibApiFunctionResolver(libAPI);

				// Kept as a fallback for a resolver that still relies on FFmpeg.AutoGen's own
				// path-based loading.
				var folders = libAPI.GetFolders();
				if (folders != null && folders.Length > 0)
					ffmpeg.RootPath = folders[0];

				LoadLibraries(libAPI);
			}

			DynamicallyLoadedBindings.Initialize();

			if (libAPI != null)
				VerifyBackend(libAPI);

			// Register network protocols (http, https, tls, …). Without this,
			// avformat_open_input on a URL fails with "Protocol not found".
			ffmpeg.avformat_network_init();

			// Only mark as initialized once the setup actually completed, so a call made too early
			// (no CoreAPI yet) does not permanently poison every later attempt.
			_initialized = true;
		}
	}
}