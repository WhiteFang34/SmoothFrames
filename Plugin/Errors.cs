using System;

namespace SmoothFrames
{
	/// <summary>
	///     Shared exception factories. Centralizing the <c>SmoothFrames:</c>
	///     prefix and stock suffixes keeps the throw site short while the
	///     literal engine-surface descriptor stays grep-able in the source.
	/// </summary>
	internal static class Errors
	{
		public static InvalidOperationException NotResolved(string what)
		{
			return new InvalidOperationException("SmoothFrames: " + what + " not resolved");
		}
	}
}
