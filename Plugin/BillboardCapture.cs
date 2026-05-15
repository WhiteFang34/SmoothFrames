using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SmoothFrames
{
	/// <summary>
	///     Sim-thread capture of the orientation inputs (origin, direction,
	///     size, vanilla camera pose) that fed each <c>Custom</c>-typed
	///     <see cref="MyBillboard"/>'s emission. The render thread consumes
	///     these via <see cref="TryGetOrient"/> to re-bake the quad against
	///     the smoothed camera each frame.
	///
	///     Split from <see cref="BillboardSmoothing"/>: capture runs once per
	///     sim emission, rebake runs once per render frame — separating the
	///     two halves leaves each file with one concern and one entry point.
	/// </summary>
	internal static class BillboardCapture
	{
		// Side table keying each MyBillboard to the orientation inputs we
		// captured at sim emission. ConditionalWeakTable lets the GC reclaim
		// entries when a MyBillboard is no longer reachable. The engine pools
		// MyBillboard instances; on reuse we Remove+Add to overwrite.
		private static readonly ConditionalWeakTable<MyBillboard, BillboardOrient> _orientTable =
			new ConditionalWeakTable<MyBillboard, BillboardOrient>();

		// Sim-thread scratch: an AddX prefix sets this, MyRenderProxy.AddBillboard
		// postfix consumes it and assigns to the just-added MyBillboard, the AddX
		// postfix clears it as a fallback if AddBillboard never ran (early return).
		[ThreadStatic] private static BillboardOrient _pendingOrient;

		public static void SetPendingPoint(Vector3D origin, uint parentId, MatrixD worldToLocal,
			float radius, float angle, int customViewProjection)
		{
			_pendingOrient = new BillboardOrient
			{
				Kind = BillboardKind.Point,
				Origin = origin,
				ParentId = parentId,
				WorldToLocal = worldToLocal,
				CustomViewProjection = customViewProjection,
				SizeX = radius,
				SizeY = radius,
				Angle = angle
			};
		}

		public static void SetPendingLine(Vector3D origin, uint parentId, MatrixD worldToLocal,
			Vector3 direction, float length, float thickness, int customViewProjection)
		{
			// Capture the full vanilla camera pose at emission. The
			// free-floating placement re-bake needs both the position delta
			// AND the rotation delta to reproject the line's endpoints — view
			// rotation between sim ticks shifts the gizmo's world origin by
			// several cm even when the player isn't translating, and a
			// position-only correction (sufficient for piloted-ship lines
			// where rotations are usually small) leaves the wireframe visibly
			// trailing the smoothed ghost block during fast view-turns.
			var vanillaCamWorld = MyTransparentGeometry.Camera;
			var camRotMat = (Matrix)vanillaCamWorld;
			camRotMat.Translation = Vector3.Zero;

			_pendingOrient = new BillboardOrient
			{
				Kind = BillboardKind.Line,
				Origin = origin,
				ParentId = parentId,
				WorldToLocal = worldToLocal,
				CustomViewProjection = customViewProjection,
				Direction = direction,
				Length = length,
				Thickness = thickness,
				VanillaCameraPos = vanillaCamWorld.Translation,
				VanillaCameraRotation = Quaternion.CreateFromRotationMatrix(camRotMat)
			};
		}

		public static void ClearPending()
		{
			_pendingOrient = null;
		}

		public static void AssignPending(MyBillboard billboard)
		{
			if (billboard == null)
			{
				return;
			}

			// Always clear any prior entry for this MyBillboard reference. The
			// engine pools instances; if an unrecorded path (AddBillboardOriented,
			// AddTriangleBillboard, …) is reusing this slot, we must not rebake
			// it using the stale orientation captured from an earlier emission.
			_orientTable.Remove(billboard);

			if (_pendingOrient != null)
			{
				_orientTable.Add(billboard, _pendingOrient);
				_pendingOrient = null;
				return;
			}

			// MyTriangleBillboard skips the Direct reverse-engineering. RHM
			// pre-allocates pooled triangle billboards and registers them via
			// MyTransparentGeometry.AddBillboard BEFORE its UpdateBillboards
			// pass writes the final Position0..2 (positions are written later
			// in FinishDraw, still on the same sim tick but after our postfix
			// fires). Reverse-engineering corners now would capture stale
			// positions from the previous use of this pool entry —
			// RebakeDirect would then transform that garbage every render
			// frame, producing the off-screen "jet off" symptom the user
			// observed on the BV3 wheel center. Triangle billboards always
			// fall through to the camera-anchored fallback at Gather time,
			// where the positions have been written and the proximity gate
			// filters world-anchored cases out.
			if (billboard is MyTriangleBillboard)
			{
				return;
			}

			// Direct path — caller filled MyBillboard fields and passed it to
			// MyRenderProxy.AddBillboard without going through
			// MyTransparentGeometry (Text HUD API, HUD Compass, etc.).
			// Reverse-engineer center and the half-side lengths from the four
			// corner positions, assuming the caller produced a
			// screen-axis-aligned face-camera quad.
			if (billboard.LocalType != MyBillboard.LocalTypeEnum.Custom
				|| billboard.ParentID != uint.MaxValue
				|| billboard.CustomViewProjection != -1)
			{
				return;
			}

			if (TryReverseEngineer(billboard, out var orient))
			{
				_orientTable.Add(billboard, orient);
			}
		}

		// Render-thread lookup. Returns true when this MyBillboard has a
		// captured orient (Point/Line/Direct) — the rebake engine then
		// dispatches by kind. False means no orient: the rebake falls through
		// to the per-ordinal lerp / camera-anchored fallback paths.
		internal static bool TryGetOrient(MyBillboard billboard, out BillboardOrient orient)
		{
			return _orientTable.TryGetValue(billboard, out orient);
		}

		private static bool TryReverseEngineer(MyBillboard bb, out BillboardOrient orient)
		{
			orient = null;

			// Capture the corners verbatim and the camera position that produced
			// them. The render thread rotates the corners around their average
			// (the billboard's world-space origin) by the rotation that maps
			// vanilla-face onto smoothed-face. Side lengths and the in-plane
			// orientation of the quad are preserved.
			var p0 = bb.Position0;
			var p1 = bb.Position1;
			var p2 = bb.Position2;
			var p3 = bb.Position3;
			var center = (p0 + p1 + p2 + p3) * 0.25;

			// Reject degenerate quads (zero-area).
			if ((p1 - p0).LengthSquared() < 1e-10 || (p3 - p0).LengthSquared() < 1e-10)
			{
				return false;
			}

			var camWorld = MyTransparentGeometry.Camera;
			var camRotMat = (Matrix)camWorld;
			camRotMat.Translation = Vector3.Zero;

			orient = new BillboardOrient
			{
				Kind = BillboardKind.Direct,
				Origin = center,
				ParentId = uint.MaxValue,
				WorldToLocal = MatrixD.Identity,
				CustomViewProjection = -1,
				DirectP0 = p0,
				DirectP1 = p1,
				DirectP2 = p2,
				DirectP3 = p3,
				VanillaCameraPos = camWorld.Translation,
				VanillaCameraRotation = Quaternion.CreateFromRotationMatrix(camRotMat)
			};
			return true;
		}
	}

	internal enum BillboardKind
	{
		// Captured at MyTransparentGeometry.AddPointBillboard.
		Point,
		// Captured at MyTransparentGeometry.AddLineBillboard.
		Line,
		// Reverse-engineered at MyRenderProxy.AddBillboard for billboards that
		// bypassed MyTransparentGeometry (e.g. Text HUD API, HUD Compass).
		Direct
	}

	internal sealed class BillboardOrient
	{
		public BillboardKind Kind;
		public Vector3D Origin;
		public uint ParentId;
		public MatrixD WorldToLocal;
		public int CustomViewProjection;

		// Point: SizeX = SizeY = Radius.
		public float SizeX;
		public float SizeY;
		public float Angle;     // Point only

		// Line
		public Vector3 Direction;
		public float Length;
		public float Thickness;

		// Direct: original four corner positions captured at emission, plus the
		// vanilla camera pose that produced them. Rebake makes each corner
		// camera-relative — translates it by the camera-position delta and
		// rotates it by the camera-rotation delta — which is correct for
		// billboards anchored to the player's view (HUD overlays).
		public Vector3D DirectP0;
		public Vector3D DirectP1;
		public Vector3D DirectP2;
		public Vector3D DirectP3;
		public Vector3D VanillaCameraPos;
		public Quaternion VanillaCameraRotation;
	}

	/// <summary>
	///     Sim-thread prefix on the full-arg
	///     <c>MyTransparentGeometry.AddPointBillboard</c> overload. Captures
	///     the orientation inputs so the render thread can re-bake the quad
	///     against the smoothed camera each frame.
	/// </summary>
	[HarmonyPatch]
	public static class PatchAddPointBillboard
	{
		public static MethodBase TargetMethod()
		{
			return AccessTools.Method(typeof(MyTransparentGeometry), "AddPointBillboard", new[]
			{
				typeof(MyStringId), typeof(Vector4), typeof(Vector3D), typeof(uint),
				typeof(MatrixD).MakeByRefType(),
				typeof(float), typeof(float),
				typeof(int), typeof(MyBillboard.BlendTypeEnum), typeof(float),
				typeof(List<MyBillboard>)
			}) ?? throw Errors.NotResolved("MyTransparentGeometry.AddPointBillboard");
		}

		public static void Prefix(Vector3D origin, uint renderObjectID, ref MatrixD worldToLocal,
			float radius, float angle, int customViewProjection)
		{
			BillboardCapture.SetPendingPoint(origin, renderObjectID, worldToLocal,
				radius, angle, customViewProjection);
		}

		public static void Postfix()
		{
			// Fallback in case AddBillboard wasn't reached (e.g. the original
			// method early-returned before allocating).
			BillboardCapture.ClearPending();
		}
	}

	/// <summary>
	///     Sim-thread prefix on the full-arg
	///     <c>MyTransparentGeometry.AddLineBillboard</c> overload.
	/// </summary>
	[HarmonyPatch]
	public static class PatchAddLineBillboard
	{
		public static MethodBase TargetMethod()
		{
			return AccessTools.Method(typeof(MyTransparentGeometry), "AddLineBillboard", new[]
			{
				typeof(MyStringId), typeof(Vector4), typeof(Vector3D), typeof(uint),
				typeof(MatrixD).MakeByRefType(),
				typeof(Vector3), typeof(float), typeof(float),
				typeof(MyBillboard.BlendTypeEnum), typeof(int), typeof(float),
				typeof(List<MyBillboard>)
			}) ?? throw Errors.NotResolved("MyTransparentGeometry.AddLineBillboard");
		}

		public static void Prefix(Vector3D origin, uint renderObjectID, ref MatrixD worldToLocal,
			Vector3 directionNormalized, float length, float thickness, int customViewProjection)
		{
			BillboardCapture.SetPendingLine(origin, renderObjectID, worldToLocal,
				directionNormalized, length, thickness, customViewProjection);
		}

		public static void Postfix()
		{
			BillboardCapture.ClearPending();
		}
	}

	/// <summary>
	///     Sim-thread postfix on <see cref="MyRenderProxy.AddBillboard"/>.
	///     Picks up the orientation set by an <c>AddX</c> prefix and binds
	///     it to the billboard reference being added.
	/// </summary>
	[HarmonyPatch(typeof(MyRenderProxy), "AddBillboard", typeof(MyBillboard))]
	public static class PatchMyRenderProxyAddBillboard
	{
		public static void Postfix(MyBillboard billboard)
		{
			BillboardCapture.AssignPending(billboard);
		}
	}
}
