using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

namespace SmoothFrames
{
	public class Plugin : IPlugin
	{
		public static readonly string Version =
			typeof(Plugin).Assembly.GetName().Version.ToString();

		/// <summary>
		///     Read by patches every render frame. Mirror of
		///     <see cref="PluginConfig.InterpolationEnabled"/> kept as a
		///     volatile field so the render-thread postfix doesn't pay a
		///     property-call cost per frame.
		/// </summary>
		public static volatile bool InterpolationEnabled = true;

		public static PluginConfig Config { get; private set; } = new PluginConfig();

		private const string ConfigFileName = "SmoothFrames.cfg";

		// Set true once TryApplyFrameRateCap returns true — i.e. the cap has
		// either been applied or the engine fields it needs aren't resolvable.
		// Either way, no point in retrying. False until the render thread's
		// WaitForTargetFrameRate exists.
		private bool _frameRateCapInitialized;

		public void Init(object gameInstance)
		{
			MyLog.Default.WriteLineAndConsole($"SmoothFrames v{Version} loaded");

			Config = PluginConfig.Load(Path.Combine(MyFileSystem.UserDataPath, ConfigFileName));
			InterpolationEnabled = Config.InterpolationEnabled;

			new Harmony("SmoothFrames").PatchAll(Assembly.GetExecutingAssembly());

			// Force type-init for non-patch helper classes that cache engine
			// reflection. PatchAll touches every [HarmonyPatch] type, but
			// helpers like these only get touched on first runtime use —
			// moving that touch here means any missing engine surface throws
			// during plugin load, not mid-frame.
			RuntimeHelpers.RunClassConstructor(typeof(AttachedLightCapture).TypeHandle);
			RuntimeHelpers.RunClassConstructor(typeof(AttachedParticleCapture).TypeHandle);
			RuntimeHelpers.RunClassConstructor(typeof(CameraCapture).TypeHandle);
			RuntimeHelpers.RunClassConstructor(typeof(RenderFrameRateCap).TypeHandle);
			RuntimeHelpers.RunClassConstructor(typeof(RenderFrameSmoothing).TypeHandle);
		}

		/// <summary>
		///     Single entry point for both the dialog checkbox and the
		///     Ctrl+F11 hotkey: updates the volatile field, persists, and
		///     keeps the config snapshot in sync.
		/// </summary>
		public static void SetInterpolationEnabled(bool value)
		{
			InterpolationEnabled = value;
			Config.InterpolationEnabled = value;
			Config.Save();
		}

		public void Dispose()
		{
			CameraHistory.Reset();
		}

		public void Update()
		{
			if (!_frameRateCapInitialized)
			{
				_frameRateCapInitialized = RenderFrameRateCap.TryApplyFrameRateCap(Config.FrameRateCap);
			}
		}

		// Detected by name by the Pulsar plugin loader, which surfaces a
		// settings cog next to the plugin in its list when this method exists.
		// Don't rename without updating Pulsar's expectations.
		public void OpenConfigDialog()
		{
			MyGuiSandbox.AddScreen(new PluginConfigDialog());
		}
	}
}
