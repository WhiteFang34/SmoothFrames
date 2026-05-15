using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Input;

namespace SmoothFrames
{
	/// <summary>
	///     Handles the user-facing toggle (Ctrl+F11 to enable/disable
	///     interpolation).
	/// </summary>
	[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
	public class SmoothSession : MySessionComponentBase
	{
		public override void LoadData()
		{
			CameraHistory.Reset();
		}

		protected override void UnloadData()
		{
			CameraHistory.Reset();
		}

		public override void HandleInput()
		{
			var input = MyInput.Static;

			// Ctrl+F11: toggle interpolation.
			if (!input.IsAnyCtrlKeyPressed() || input.IsAnyShiftKeyPressed() || input.IsAnyAltKeyPressed()
				|| !input.IsNewKeyPressed(MyKeys.F11))
			{
				return;
			}

			Plugin.SetInterpolationEnabled(!Plugin.InterpolationEnabled);
			MyAPIGateway.Utilities?.ShowNotification(
				"Smooth Frames: interpolation " + (Plugin.InterpolationEnabled ? "ON" : "OFF"));
		}
	}
}
