using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game;
using VRageMath;

namespace SmoothFrames
{
	/// <summary>
	///     The block-placement preview (the transparent ghost block shown
	///     while the player has a block selected and is choosing where to
	///     place it) is rendered through MyBlockBuilderRenderData's render
	///     entities, *not* through MyEntity-wrapped objects, so the existing
	///     per-entity smoothing path doesn't catch it.
	///
	///     <see cref="MyCubeBuilder.Draw"/> runs at SIM rate (60 Hz) from
	///     <see cref="Sandbox.Game.World.MySession.Draw"/> on the update
	///     thread, *not* per render frame. Modifying gridWorldMatrix in a
	///     prefix on EndCollectingInstanceData only changes the value pushed
	///     once per sim tick, after which the renderer reuses that same
	///     matrix for every interpolated render frame in between — the
	///     preview snaps to sim-tick poses while the rest of the scene moves
	///     smoothly, which is the visible ghosting in free-floating mode.
	///
	///     Instead we mirror the held-tool pattern: capture each preview
	///     render entity's vanilla world matrix on the sim thread (postfix
	///     on the static struct method
	///     MyBlockBuilderRenderData+MyRenderEntity.Update which the engine
	///     calls per entity inside EndCollectingInstanceData), then on the
	///     render thread per render frame in
	///     <see cref="RenderFrameSmoothing.RunFrame"/> reapply each captured
	///     matrix post-multiplied by inverse(vanilla_cam) * smoothed_cam,
	///     pushed synchronously through the PostponedUpdate bypass after the
	///     message queue has drained. The smoothed camera is recomputed fresh
	///     each render frame so the preview tracks the camera sub-tick.
	///
	///     Scoped to free-floating placement (<c>CurrentGrid == null</c>).
	///     Snapped placement is skipped: the grid is independent of the
	///     camera (on foot) or already smoothed via the entity path
	///     (piloting), so applying the camera delta would shift the gizmo
	///     off the grid face.
	/// </summary>
	internal static class PlacementPreviewCapture
	{
		internal struct Entry
		{
			public uint RenderEntityId;
			public MatrixD VanillaWorldMatrix;
		}

		// Sim-thread snapshot published atomically at the end of each sim
		// tick's EndCollectingInstanceData call. Render thread reads via the
		// volatile field — single reference assignment, no lock needed. The
		// vanilla camera world matrix is captured alongside the entries so
		// the render-thread delta isn't taken against
		// MyTransparentGeometry.Camera (which could already be a newer sim
		// tick's value by the time the render thread runs).
		internal sealed class Snapshot
		{
			public MatrixD VanillaCameraWorld;
			public Entry[] Entries;
		}

		// Sim-thread scratch — cleared in EndCollectingInstanceData prefix,
		// populated by per-entity Update postfix calls inside it, committed in
		// EndCollectingInstanceData postfix.
		private static readonly List<Entry> _scratch = new List<Entry>(4);

		// Volatile so the render thread sees the latest committed snapshot
		// without locking. Snapshot instances are treated as immutable once
		// published.
		private static volatile Snapshot _published;

		// Sim-thread flag set in the EndCollectingInstanceData prefix and read
		// in the per-entity Update postfix to decide whether this tick's
		// pushes are eligible for capture. Avoids re-running the anchor
		// discrimination per entity.
		internal static bool ShouldCapture;

		public static Snapshot Read()
		{
			return _published;
		}

		public static bool HasEntries
		{
			get
			{
				var pub = _published;
				return pub?.Entries != null && pub.Entries.Length > 0;
			}
		}

		public static void BeginTick()
		{
			_scratch.Clear();
		}

		public static void CaptureEntity(uint id, MatrixD world)
		{
			_scratch.Add(new Entry { RenderEntityId = id, VanillaWorldMatrix = world });
		}

		public static void Commit(MatrixD vanillaCameraWorld)
		{
			if (_scratch.Count == 0)
			{
				_published = null;
				return;
			}
			_published = new Snapshot
			{
				VanillaCameraWorld = vanillaCameraWorld,
				Entries = _scratch.ToArray()
			};
		}
	}

	/// <summary>
	///     Sim-thread bracket on
	///     <c>MyBlockBuilderRenderData.EndCollectingInstanceData</c>. Prefix
	///     clears the scratch list and decides whether this tick's per-entity
	///     pushes should be captured (only when the gizmo is camera-anchored
	///     in free-floating mode). Postfix commits the captured list as the
	///     new render-thread snapshot, paired with the vanilla camera world
	///     matrix at this tick.
	/// </summary>
	[HarmonyPatch]
	public static class PatchEndCollectingInstanceData
	{
		// CurrentGrid is protected internal on MyCubeBuilder; reflect.
		private static readonly PropertyInfo _currentGridProp =
			AccessTools.Property(typeof(MyCubeBuilder), "CurrentGrid")
			?? throw Errors.NotResolved("MyCubeBuilder.CurrentGrid");

		public static MethodBase TargetMethod()
		{
			return AccessTools.Method("Sandbox.Game.Entities.Cube.MyBlockBuilderRenderData:EndCollectingInstanceData",
					new[] { typeof(MatrixD), typeof(bool) })
				?? throw Errors.NotResolved("MyBlockBuilderRenderData.EndCollectingInstanceData");
		}

		public static void Prefix()
		{
			PlacementPreviewCapture.BeginTick();
			PlacementPreviewCapture.ShouldCapture = false;

			var cubeBuilder = MyCubeBuilder.Static;
			if (cubeBuilder == null)
			{
				return;
			}

			// Free-floating placement: CurrentGrid == null. Gizmo is
			// camera-anchored, so the camera delta is the right correction.
			// Snapped placement is skipped — the grid is independent of the
			// camera (on foot) or already smoothed via the entity path
			// (piloting, not yet observed to ghost in this case).
			var currentGrid = _currentGridProp.GetValue(cubeBuilder);
			if (currentGrid != null)
			{
				return;
			}

			PlacementPreviewCapture.ShouldCapture = true;
		}

		public static void Postfix()
		{
			// MyTransparentGeometry.Camera = MySector.MainCamera.WorldMatrix;
			// at this point in the sim tick it holds the sim-tick T camera
			// the gizmo matrix was derived from upstream of this call. Pair it
			// with the captured matrices so the render thread takes its delta
			// against the right vanilla pose, even if a new sim tick has
			// overwritten MyTransparentGeometry.Camera by the time the render
			// thread reads.
			PlacementPreviewCapture.Commit(MyTransparentGeometry.Camera);

			// HasEntries is the load-bearing signal: if the engine produced
			// preview render entities this tick AND we were in free-floating
			// mode (the prefix's CurrentGrid == null gate), the gizmo's
			// green/red wireframe is also in flight as line billboards and
			// needs the same camera-delta treatment.
			// BillboardSmoothing.RebakeLine reads the flag on the render
			// thread to opt into the camera-anchored long-span path.
			BillboardSmoothing.SetFreeFloatingPlacement(PlacementPreviewCapture.HasEntries);

			PlacementPreviewCapture.ShouldCapture = false;
		}
	}

	/// <summary>
	///     Sim-thread postfix on the static struct method
	///     <c>MyBlockBuilderRenderData+MyRenderEntity.Update(ref MyRenderEntity,
	///     ref MatrixD, float)</c>. The engine just computed
	///     <c>value = entity.LocalMatrix * gridWorldMatrix</c> and queued it
	///     via <c>MyRenderProxy.UpdateRenderObject</c>; we record the same
	///     world matrix so the render thread can re-issue it against the
	///     smoothed camera each render frame. Re-doing the multiply in the
	///     postfix avoids needing to read the engine's queued message.
	///
	///     <c>MyRenderEntity</c> is a private nested struct, so we resolve
	///     the type by name and read its public fields via reflection. The
	///     param-list types we hand to AccessTools must be byref to match
	///     the ref-struct/ref-MatrixD signature; passing the plain struct
	///     type would resolve to no method.
	/// </summary>
	[HarmonyPatch]
	public static class PatchMyRenderEntityUpdate
	{
		private static readonly Type _myRenderEntityType =
			AccessTools.TypeByName("Sandbox.Game.Entities.Cube.MyBlockBuilderRenderData+MyRenderEntity")
			?? throw Errors.NotResolved("MyBlockBuilderRenderData+MyRenderEntity");

		private static readonly FieldInfo _renderEntityIdField = _myRenderEntityType.GetField("RenderEntityId")
			?? throw Errors.NotResolved("MyRenderEntity.RenderEntityId");

		private static readonly FieldInfo _localMatrixField = _myRenderEntityType.GetField("LocalMatrix")
			?? throw Errors.NotResolved("MyRenderEntity.LocalMatrix");

		public static MethodBase TargetMethod()
		{
			return AccessTools.Method(_myRenderEntityType, "Update", new[]
			{
				_myRenderEntityType.MakeByRefType(),
				typeof(MatrixD).MakeByRefType(),
				typeof(float)
			}) ?? throw Errors.NotResolved("MyBlockBuilderRenderData+MyRenderEntity.Update");
		}

		public static void Postfix(object[] __args)
		{
			if (!PlacementPreviewCapture.ShouldCapture)
			{
				return;
			}

			if (__args == null || __args.Length < 2)
			{
				return;
			}

			// __args[0] = boxed MyRenderEntity struct, __args[1] = boxed
			// MatrixD gridWorldMatrix. Harmony boxes ref-passed value types
			// for __args injection; this is a snapshot copy (modifying
			// __args[0] would not propagate back), which is fine because we're
			// only reading.
			var entity = __args[0];
			if (entity == null)
			{
				return;
			}

			var id = (uint)_renderEntityIdField.GetValue(entity);
			var localF = (Matrix)_localMatrixField.GetValue(entity);
			var gridWorld = (MatrixD)__args[1];

			MatrixD localD = localF;
			var world = localD * gridWorld;

			PlacementPreviewCapture.CaptureEntity(id, world);
		}
	}
}
