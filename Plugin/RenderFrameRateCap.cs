using System.Reflection;
using HarmonyLib;
using VRage.Library.Utils;
using VRageRender;
using VRageRender.ExternalApp;

namespace SmoothFrames
{
	/// <summary>
	///     Lifts SE's vanilla 120 FPS render-thread cap by rewriting the
	///     target frequency on <c>MyRenderThread.m_waiter</c>. Drive from
	///     <c>IPlugin.Update</c> until it returns true (cap applied); after
	///     that, re-apply with a new value any time the user changes the
	///     slider.
	/// </summary>
	public static class RenderFrameRateCap
	{
		private static readonly FieldInfo _waiterField = AccessTools.Field(typeof(MyRenderThread), "m_waiter")
			?? throw Errors.NotResolved("MyRenderThread.m_waiter");

		private static readonly FieldInfo _targetFreqField =
			AccessTools.Field(typeof(WaitForTargetFrameRate), "m_targetFrequency")
			?? throw Errors.NotResolved("WaitForTargetFrameRate.m_targetFrequency");

		public static bool TryApplyFrameRateCap(int targetFps)
		{
			var renderThread = MyRenderProxy.RenderThread;
			if (renderThread == null)
			{
				return false;
			}

			if (!(_waiterField.GetValue(renderThread) is WaitForTargetFrameRate waiter))
			{
				return false;
			}

			_targetFreqField.SetValue(waiter, (float)targetFps);
			return true;
		}
	}
}
