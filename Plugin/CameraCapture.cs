using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.ModAPI;
using VRageMath;
using VRageRender;

namespace SmoothFrames
{
	/// <summary>
	///     Harmony postfix on <c>MyCamera.UploadViewMatrixToRender</c>.
	///     Delegates to <see cref="CameraCapture"/>; the original method is
	///     *not* suppressed — vanilla still enqueues a real camera message
	///     each tick, we just record alongside.
	/// </summary>
	[HarmonyPatch(typeof(MyCamera), "UploadViewMatrixToRender")]
	public static class PatchUploadViewMatrixToRender
	{
		public static void Postfix(MyCamera __instance)
		{
			CameraCapture.SnapAndDefer(__instance);
		}
	}

	/// <summary>
	///     Sim-thread postfix on <see cref="MyRenderProxy.AfterUpdate"/>,
	///     which forwards to <c>MySharedData.AfterUpdate</c> — the only
	///     per-tick site that calls <c>m_inputBillboards.CommitWrite()</c>.
	///     Inside <c>MySandboxGame.UpdateInternal</c> a sim tick runs:
	///     <c>Update → PrepareForDraw → AfterDraw</c>; <c>AfterDraw</c> on
	///     <c>MySandboxExternal</c> calls <see cref="MyRenderProxy.AfterUpdate"/>.
	///     Billboards emitted during the tick (e.g.,
	///     <see cref="VRage.Game.MyTransparentGeometry"/><c>.AddLineBillboard</c>)
	///     accumulate into the swap queue's write side and don't become
	///     visible to the render thread until that <c>CommitWrite</c> fires.
	///     <para>
	///         <see cref="CameraCapture"/> captures the snapshot earlier (in
	///         <c>MyCamera.UploadViewMatrixToRender</c>'s postfix during
	///         <c>PrepareForDraw</c>) but defers the
	///         <see cref="CameraHistory.Push"/> until this postfix runs —
	///         pairing snapshot T with billboards T on the render thread,
	///         instead of leaving a window where the render thread sees snap
	///         T paired with billboards T-1.
	///     </para>
	///     <para>
	///         The misaligned-pair window was the cause of the
	///         depth-pulled-hairline flicker on moving ships:
	///         <c>orient.VanillaCameraPos</c> from tick T-1 didn't match
	///         the moving-grid registry built from snap T, so the
	///         recovered target landed at the wrong vanilla pose and fell
	///         outside the grid's <c>WorldVolume</c> sphere on most render
	///         frames.
	///     </para>
	/// </summary>
	[HarmonyPatch]
	public static class PatchAfterUpdate
	{
		public static MethodBase TargetMethod()
		{
			return AccessTools.Method(typeof(MyRenderProxy), "AfterUpdate")
				?? throw Errors.NotResolved("MyRenderProxy.AfterUpdate");
		}

		public static void Postfix()
		{
			CameraCapture.PublishPendingSnap();
		}
	}

	/// <summary>
	///     Sim-thread capture of the camera + nearby moving entities at each
	///     <c>MyCamera.UploadViewMatrixToRender</c> call. The snapshot is
	///     staged in <c>_pendingSnap</c> and published to
	///     <see cref="CameraHistory"/> only after
	///     <see cref="MyRenderProxy.AfterUpdate"/> fires later in the same
	///     sim tick — at which point the render thread's billboard read-side
	///     has just received this tick's <c>m_inputBillboards.CommitWrite</c>,
	///     so the deferred publish pairs snapshot T with billboards T
	///     atomically.
	///
	///     Driven by <see cref="PatchUploadViewMatrixToRender"/> (capture)
	///     and <see cref="PatchAfterUpdate"/> (publish).
	/// </summary>
	internal static class CameraCapture
	{
		// Sim-thread-only: each entity's last captured world matrix. Lets us
		// pair the new tick's matrix with the previous tick's so the render
		// thread can interpolate between them. Indexed by EntityId.
		private static readonly Dictionary<long, MatrixD> _lastSeenMatrix = new Dictionary<long, MatrixD>();

		// Reused list to avoid an allocation each sim tick. Materialized to a
		// fresh array on Push so the snapshot is immutable to the render side.
		private static readonly List<SmoothedEntity> _scratchEntities = new List<SmoothedEntity>(8);

		// Dedupes the recursion. An animated subpart can be reachable via both
		// `Hierarchy.Children` and `Subparts.Values`, and capturing it twice
		// caused the second record to read prev=curr from the dictionary (the
		// first record had just written it), zeroing its motion — and since
		// both records hit the same render-object ID the no-motion write
		// landed last and overwrote the smoothed one (drill spike / grinder
		// wheel ghosting).
		private static readonly HashSet<long> _seenEntityIds = new HashSet<long>();

		// Scratch buffer for the per-tick world-entity enumeration. Cleared and
		// repopulated each tick. Held as a static so we don't reallocate.
		private static readonly HashSet<IMyEntity> _worldEntityScratch = new HashSet<IMyEntity>();

		// Scratch buffer for evicting stale prev-pose entries. Sized to match
		// _lastSeenMatrix shrinkage; cleared each tick.
		private static readonly List<long> _evictScratch = new List<long>();

		// Per-entity scratch list filled by AttachedParticleCapture.Collect
		// during AddEntity; materialized to a fresh AttachedParticle[] on the
		// SmoothedEntity. Capacity 4 covers the worst vanilla case (welder:
		// flame + tool primary + tool secondary = 3) without a growth
		// allocation.
		private static readonly List<AttachedParticle> _attachedScratch = new List<AttachedParticle>(4);

		// Same shape for AttachedLightCapture. Capacity 2 covers a single
		// entity's worst vanilla case (the local character contributes one
		// headlamp; a held welder contributes one tool effect light); a
		// future entity that owns multiple unparented lights would still
		// fit without growth.
		private static readonly List<AttachedLightSnapshot> _lightScratch = new List<AttachedLightSnapshot>(2);

		// Radius around the camera (in metres, squared) within which we capture
		// nearby moving grids and characters. Beyond this they're tiny on
		// screen and the wobble is invisible — saves iterating every entity in
		// big multiplayer worlds.
		private const double NearbyMovableRadiusSq = 5000.0 * 5000.0;

		// MyRenderComponentCharacter parents the character's render object to a
		// ManualCull object via SetParent(0, m_cullRenderId, Matrix.Identity).
		// Sim sends UpdateRenderObject only to the cull parent; each render
		// frame, MyInstance.UpdateWorldMatrix recomputes child.LastWorldMatrix
		// = relative * Parent.LastWorldMatrix, so a write to the child's
		// render id gets clobbered every frame from the unsmoothed parent.
		// Redirect to the cull parent and the child follows automatically.
		private static readonly Type _characterRenderComponentType =
			AccessTools.TypeByName("Sandbox.Game.Components.MyRenderComponentCharacter")
			?? throw Errors.NotResolved("Sandbox.Game.Components.MyRenderComponentCharacter");
		private static readonly FieldInfo _characterCullRenderIdField =
			AccessTools.Field(_characterRenderComponentType, "m_cullRenderId")
			?? throw Errors.NotResolved("MyRenderComponentCharacter.m_cullRenderId");

		// Snapshot captured by SnapAndDefer, held here until PublishPendingSnap
		// is called from PatchAfterUpdate.Postfix.
		private static CameraSnapshot _pendingSnap;
		private static bool _hasPendingSnap;

		internal static void SnapAndDefer(MyCamera camera)
		{
			// Pull rotation off the world matrix. World = Invert(View), so
			// WorldMatrix.Translation is the camera world position and the
			// 3x3 rotation basis is camera orientation.
			var world = camera.WorldMatrix;
			var rotationOnly = (Matrix)world;
			rotationOnly.Translation = Vector3.Zero;

			// Re-entry guard for plugins (HeadTracking, HeadTrackingPlus) that
			// postfix MySession.DrawSync to apply head rotation/offset and
			// explicitly invoke MyCamera.UploadViewMatrixToRender a second
			// time within the same sim tick — after Draw3DScene has already
			// queued render messages against the vanilla pose, but before
			// MyRenderProxy.AfterUpdate flushes the frame. Re-running
			// CollectSmoothedEntities on the second call would re-read
			// _lastSeenMatrix entries that the first call just overwrote with
			// this tick's poses, producing prev=curr SmoothedEntity records
			// (zero entity motion). The piloted ship interior, held tool, and
			// nearby grids would then stutter at sim rate while the camera
			// kept smoothing — visible as the cockpit wobbling against the
			// smoothed view at high refresh.
			//
			// On re-entry, just refresh the camera-pose fields so the snapshot
			// reflects the head-tracked view that the renderer will actually
			// draw, and keep the SmoothedEntities list (and the
			// _lastSeenMatrix it was built from) intact. The signal that we're
			// re-entering is _hasPendingSnap already being true —
			// PublishPendingSnap clears it at AfterUpdate, so the flag
			// accurately tracks "have we captured this tick yet." If a third
			// plugin chains another late upload after these, the same path
			// takes — last write wins for the camera, entities still smooth
			// correctly.
			if (_hasPendingSnap)
			{
				FillCameraFields(ref _pendingSnap, camera, ref world, ref rotationOnly);
				return;
			}

			CameraSnapshot snap = default;
			FillCameraFields(ref snap, camera, ref world, ref rotationOnly);
			snap.SmoothedEntities = CollectSmoothedEntities(world.Translation);

			// Defer the publish — see _pendingSnap doc above. The actual
			// CameraHistory.Push happens in PublishPendingSnap after the
			// billboards from this tick are committed.
			_pendingSnap = snap;
			_hasPendingSnap = true;
		}

		// Called from PatchAfterUpdate.Postfix after MyRenderProxy.AfterUpdate
		// has run (and thus after m_inputBillboards.CommitWrite for this tick).
		// Pushes the most recent capture into CameraHistory so the next render
		// frame sees a matched (snapshot, billboards) pair from the same sim
		// tick.
		internal static void PublishPendingSnap()
		{
			if (!_hasPendingSnap)
			{
				return;
			}

			var snap = _pendingSnap;
			_hasPendingSnap = false;
			CameraHistory.Push(ref snap);
		}

		private static void FillCameraFields(ref CameraSnapshot snap, MyCamera camera,
			ref MatrixD world, ref Matrix rotationOnly)
		{
			snap.TimestampTicks = Stopwatch.GetTimestamp();
			snap.Position = world.Translation;
			snap.Rotation = Quaternion.CreateFromRotationMatrix(rotationOnly);
			snap.Fov = camera.FovWithZoom;
			snap.FovForSkybox = camera.FovWithZoom;
			snap.ProjectionMatrix = camera.ProjectionMatrix;
			snap.ProjectionMatrixFar = camera.ProjectionMatrixFar;
			snap.NearPlane = camera.NearPlaneDistance;
			snap.FarPlane = camera.FarPlaneDistance;
			snap.FarFarPlane = camera.FarFarPlaneDistance;
			snap.Smooth = camera.SmoothMotion;
		}

		// Capture entities the renderer should keep in lockstep with the
		// smoothed camera by interpolating their world matrix between the
		// previous and current sim-tick poses.
		private static SmoothedEntity[] CollectSmoothedEntities(Vector3D cameraPos)
		{
			var session = MySession.Static;
			if (session == null)
			{
				return null;
			}

			_scratchEntities.Clear();
			_seenEntityIds.Clear();
			AttachedLightCapture.BeginTick();

			var character = session.LocalCharacter;
			MyEntity characterEntity = character;

			// Local character + its children (helmet, jetpack, etc.). Without
			// the children, attached parts ghost relative to the smoothed
			// character body when the player moves fast.
			AddEntity(characterEntity, recurseChildren: true);

			// Held weapon — separate top-level entity, not parented to the
			// character. Recurse so animated sub-parts (drill spike, grinder
			// wheel) are captured too.
			AddEntity(character?.CurrentWeapon as MyEntity, recurseChildren: true);

			// When piloting, the camera tracks the controlled entity's top-most
			// parent (the ship grid). Smooth that grid so the cockpit interior
			// doesn't oscillate. Skip when the controlled entity is the
			// character itself — already covered above.
			//
			// recurseChildren: true walks the grid's `Hierarchy.Children`,
			// which holds every fat block (engine pairs
			// `Hierarchy.AddChild(fatBlock)` with `m_fatBlocks.Add(...)` in
			// MyCubeGrid). The recursion then walks each block's
			// `Subparts.Values` — picking up animated block subparts
			// (parachute canopy, piston rod, turret barrel, etc.) so they
			// track the smoothed grid pose between sim ticks. The render-id
			// check inside AddEntity filters blocks that share the grid's
			// combined mesh (no own render object), so the cost is dominated
			// by the few blocks that actually have unique render IDs.
			var controlledEntity = session.ControlledEntity?.Entity;
			var piloting = controlledEntity != null && controlledEntity != characterEntity;
			if (piloting)
			{
				AddEntity(controlledEntity.GetTopMostParent(), recurseChildren: true, isPilotedShip: true);
			}

			// BillboardSmoothing's RebakeLine uses this to decide whether
			// world-anchored line billboards should follow the smoothed camera
			// (true: lines belong to the piloted ship; on foot they should
			// stay put).
			BillboardSmoothing.SetPilotingState(piloting);

			// Other moving grids and characters within view distance. Without
			// these, an external ship in motion (or another character) ghosts
			// against the smoothed scene the same way the local character did
			// before the cull-parent fix — they're independently-moving
			// entities the camera doesn't track.
			AddNearbyMovableEntities(cameraPos);

			// Drop prev-pose entries for entities the collector didn't visit
			// this tick (tools no longer held, grids that left the 5 km
			// radius, despawned entities). Otherwise _lastSeenMatrix grows by
			// one entry per distinct EntityId touched over the lifetime of
			// the session. Done after collection so a re-visited entity in
			// the same tick keeps its prev pose; the early-return
			// null-session branch above skips this so a transient null
			// session doesn't nuke history.
			EvictStalePrevPoses();

			return _scratchEntities.Count == 0 ? null : _scratchEntities.ToArray();
		}

		private static void EvictStalePrevPoses()
		{
			_evictScratch.Clear();
			foreach (var key in _lastSeenMatrix.Keys)
			{
				if (!_seenEntityIds.Contains(key))
				{
					_evictScratch.Add(key);
				}
			}

			foreach (var t in _evictScratch)
			{
				_lastSeenMatrix.Remove(t);
			}

			// Same eviction policy for attached-light prev-pose, keyed by
			// light render id (not entity id) since lights move between
			// hosts as tools are picked up / put down. AttachedLightCapture
			// tracks its own per-tick "seen" set populated by Collect.
			AttachedLightCapture.EvictStale();
		}

		private static void AddNearbyMovableEntities(Vector3D cameraPos)
		{
			var entities = MyAPIGateway.Entities;
			if (entities == null)
			{
				return;
			}

			_worldEntityScratch.Clear();
			entities.GetEntities(_worldEntityScratch);

			foreach (var ient in _worldEntityScratch)
			{
				var isGrid = ient is VRage.Game.ModAPI.IMyCubeGrid;
				var isChar = ient is VRage.Game.ModAPI.IMyCharacter;
				if (!isGrid && !isChar)
				{
					continue;
				}

				// Only top-level entities — children would either be reached via
				// recursion below (for the character) or are blocks owned by a
				// grid we'll capture as a whole.
				if (ient.Parent != null)
				{
					continue;
				}

				if (Vector3D.DistanceSquared(ient.WorldMatrix.Translation, cameraPos) > NearbyMovableRadiusSq)
				{
					continue;
				}

				// AddEntity dedupes via _seenEntityIds, so the local character
				// and the controlled grid (already added explicitly above) are
				// skipped here. Recurse on grids too — same reasoning as the
				// piloted branch (catches animated block subparts on a passing
				// ship: a deployed parachute on a coasting craft, an extending
				// piston, a tracking turret).
				AddEntity(ient as MyEntity, recurseChildren: true);
			}
		}

		private static void AddEntity(MyEntity entity, bool recurseChildren, bool isPilotedShip = false)
		{
			if (entity == null)
			{
				return;
			}

			// Skip entities we've already visited this tick. See _seenEntityIds.
			if (!_seenEntityIds.Add(entity.EntityId))
			{
				return;
			}

			var renderComp = entity.Render;
			var ids = renderComp?.RenderObjectIDs;

			// Redirect characters to their cull parent (see _characterCullRenderIdField).
			var isCharacterRenderComp = renderComp != null
				&& _characterRenderComponentType.IsInstanceOfType(renderComp);
			if (isCharacterRenderComp)
			{
				var cullId = (uint)_characterCullRenderIdField.GetValue(renderComp);
				if (cullId != uint.MaxValue)
				{
					ids = new[] { cullId };
				}
			}

			var isGrid = entity is VRage.Game.ModAPI.IMyCubeGrid;

			// MyCubeGridRenderCell vacates a removed cell's slot in
			// MyRenderComponentCubeGrid.RenderObjectIDs by writing
			// uint.MaxValue in place — the array is not compacted — so a
			// grid whose first render cell was removed (block destroyed,
			// cluster reshuffled) sits with `ids[0] == uint.MaxValue` while
			// later slots stay valid until a future AddRenderObjectId scans
			// the array and reuses the vacated slot. Checking only `ids[0]`
			// would silently skip such a grid: its mesh would stop being
			// smoothed and — when it's the piloted ship — the crosshair's
			// piloted-ship correction would go missing on those frames,
			// visible as a 1-frame reticle jump during turns (the
			// per-render-frame mutation falls through and the atlas snaps to
			// the sim-rate vanilla position for that one frame). Scan the
			// whole array for any valid ID.
			var hasAnyValidId = false;
			if (ids != null)
			{
				for (var i = 0; i < ids.Length; i++)
				{
					if (ids[i] != uint.MaxValue)
					{
						hasAnyValidId = true;
						break;
					}
				}
			}

			// Piloted ship: capture the prev/curr pose even when no render
			// IDs are valid — the crosshair / artificial-horizon code needs
			// the rigid correction derived from the pose, not the IDs
			// themselves. The render-smoothing loop in RunFrame iterates
			// RenderObjectIds and skips uint.MaxValue, so an empty array is
			// a safe no-op for the mesh-override path while keeping the
			// correction available.
			if (hasAnyValidId || (isGrid && isPilotedShip))
			{
				var currMatrix = entity.WorldMatrix;
				if (!_lastSeenMatrix.TryGetValue(entity.EntityId, out var prevMatrix))
				{
					// First time we've seen this entity — fall back to no motion
					// until we have two snapshots to interpolate.
					prevMatrix = currMatrix;
				}

				_lastSeenMatrix[entity.EntityId] = currMatrix;

				// Capture grid bounding spheres so the render thread can detect
				// billboards whose world origin lies on this grid (Build Info
				// overlays, terminal underlays, etc.) and apply the grid's
				// rigid pose correction to keep them welded to the smoothed
				// grid mesh. Filter to grids — characters' small spheres are
				// dominated by held tools / nearby blocks and the character
				// already smooths via the per-render-object PostponedUpdate
				// path; spatial-routing billboards through the character would
				// risk overriding HUD-attached lines incorrectly.
				var volume = isGrid && entity.PositionComp != null
					? entity.PositionComp.WorldVolume
					: default;

				// Capture every unparented particle effect reachable from this
				// entity, sim-thread paired with its vanilla pose. The render
				// thread later applies the same rigid correction
				// (effect_smoothed = effect_T * inv(entity_T) * entity_smoothed)
				// per captured effect. Discovery is generic: see
				// AttachedParticleCapture for the field-walk strategy and
				// SmoothedEntity.AttachedParticles for the race this avoids.
				_attachedScratch.Clear();
				AttachedParticleCapture.Collect(entity, _attachedScratch);
				var attached = _attachedScratch.Count == 0
					? null
					: _attachedScratch.ToArray();

				// Unparented MyLights tied to this entity (character
				// headlamp, welder/grinder tool effect light). Materialized
				// the same way as AttachedParticles — snapshot is paired
				// with the entity's own prev/curr matrices on the same
				// record so the render thread iterates one array per
				// entity.
				_lightScratch.Clear();
				AttachedLightCapture.Collect(entity, _lightScratch);
				var lights = _lightScratch.Count == 0
					? null
					: _lightScratch.ToArray();

				DecomposeRotationAndScale(ref prevMatrix, out var prevRot, out var prevScale);
				DecomposeRotationAndScale(ref currMatrix, out var currRot, out var currScale);

				_scratchEntities.Add(new SmoothedEntity
				{
					EntityId = entity.EntityId,
					RenderObjectIds = hasAnyValidId ? ids : Array.Empty<uint>(),
					PreviousPosition = prevMatrix.Translation,
					PreviousRotation = prevRot,
					PreviousScale = prevScale,
					CurrentPosition = currMatrix.Translation,
					CurrentRotation = currRot,
					CurrentScale = currScale,
					IsGrid = isGrid,
					IsPilotedShip = isGrid && isPilotedShip,
					CurrentVolume = volume,
					AttachedParticles = attached,
					AttachedLights = lights
				});
			}

			if (!recurseChildren)
			{
				return;
			}

			if (entity.Hierarchy != null)
			{
				foreach (var childComponent in entity.Hierarchy.Children)
				{
					AddEntity(childComponent.Entity as MyEntity, recurseChildren: true);
				}
			}

			// MyEntity.Subparts is populated from subpart_* model dummies (drill
			// spike, grinder wheel, character jetpack, …). Engine code only sets
			// the subpart's Hierarchy.Parent, never AddChild — so subparts are
			// *not* in Hierarchy.Children and need a separate traversal.
			if (entity.Subparts == null)
			{
				return;
			}

			foreach (var subpart in entity.Subparts.Values)
			{
				AddEntity(subpart, recurseChildren: true);
			}
		}

		// Per-axis basis-vector lengths give the scale; dividing each row by
		// its length yields the orthonormal pure-rotation matrix that
		// `Quaternion.CreateFromRotationMatrix` needs to extract a valid
		// rotation. Without this step the parachute canopy
		// (`m_lastParachuteScale` per-tick billowing) reads back as a
		// degenerate quaternion → matrix that renders flat. VRageMath has no
		// `Decompose`, so we roll the SRT split here. Negative determinant
		// (mirroring) is rare in entity world matrices and not handled —
		// would need a sign flip on one row before the rotation extract.
		private static void DecomposeRotationAndScale(ref MatrixD m, out Quaternion rotation, out Vector3 scale)
		{
			var r0 = new Vector3((float)m.M11, (float)m.M12, (float)m.M13);
			var r1 = new Vector3((float)m.M21, (float)m.M22, (float)m.M23);
			var r2 = new Vector3((float)m.M31, (float)m.M32, (float)m.M33);

			scale = new Vector3(r0.Length(), r1.Length(), r2.Length());

			var rotMat = scale.X > 1e-6f && scale.Y > 1e-6f && scale.Z > 1e-6f
				? new Matrix(
					r0.X / scale.X, r0.Y / scale.X, r0.Z / scale.X, 0,
					r1.X / scale.Y, r1.Y / scale.Y, r1.Z / scale.Y, 0,
					r2.X / scale.Z, r2.Y / scale.Z, r2.Z / scale.Z, 0,
					0, 0, 0, 1)
				: Matrix.Identity;

			rotation = Quaternion.CreateFromRotationMatrix(rotMat);
		}
	}
}
