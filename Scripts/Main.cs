using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;

namespace Nox.FFmpeg.Executor {
	public class Main : IMainModInitializer {
		public static IModCoreAPI CoreAPI;

		public void OnInitializeMain(IMainModCoreAPI api)
			=> CoreAPI  = api;

		public void OnDisposeMain() 
			=> CoreAPI = null;
	}
}