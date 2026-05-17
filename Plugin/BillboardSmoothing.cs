using System;
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
	///     Re-bakes <see cref="MyBillboard"/> instances of
	///     <see cref="MyBillboard.LocalTypeEnum.Custom"/> against the smoothed
	///     camera position each render frame. Without this, billboards whose
	///     orientation was baked at sim emission time using the un-smoothed
	///     <see cref="MyTransparentGeometry.Camera"/> drift visibly against
	///     the smoothed scene during fast movement.
	///
	///     <see cref="MyBillboard.LocalTypeEnum.Line"/> and
	///     <see cref="MyBillboard.LocalTypeEnum.Point"/> already use the
	///     smoothed camera at render-time vertex generation, so they aren't
	///     touched here.
	///
	///     Sim-side capture lives in <see cref="BillboardCapture"/>; per-frame
	///     moving-grid corrections live in
	///     <see cref="BillboardCorrections"/>. This file owns the
	///     render-thread rebake engine — frame state, ordinal-keyed lerp
	///     cache, camera-anchored persistent fallback, and the kind-specific
	///     Rebake* methods reached from the <c>Gather</c> prefix.
	/// </summary>
	internal static class BillboardSmoothing
	{
		// Cached delegate so each Gather doesn't re-allocate when handing the
		// rebake callback to ApplyActionOnPersistentBillboards.
		private static readonly Action<MyBillboard> _rebakeBillboard = RebakeOne;

		// Smoothed camera position for the current Gather pass. Set at the
		// start of RebakeBeforeGather and read by RebakeOne via the
		// persistent-billboard callback (which can't take extra args).
		private static Vector3D _currentSmoothedCameraPos;

		// Vanilla (sim-tick) camera pose at the most recent sim tick — the same
		// pose mods like Rich HUD Master use to bake their HUD billboards via
		// MyAPIGateway.Session.Camera.WorldMatrix. Read by the camera-anchored
		// fallback in RebakeOne to compute the per-vertex rebake delta. Set
		// alongside _currentSmoothedCameraPos at the start of RebakeBeforeGather.
		private static Vector3D _currentVanillaCameraPos;
		private static MatrixD _currentVanillaToSmoothedRot;
		private static bool _currentDeltaValid;

		// Per-billboard cache of vanilla Position0..3, used by the
		// persistent-billboard fallback. The transform is destructive — it
		// rewrites Position0..3 to the smoothed-camera-frame equivalent —
		// so re-applying it across render frames within the same sim tick
		// would compound; the cached entry carries the sim tick it was
		// captured against, and on tick advance we re-capture in place.
		// Persistent billboards have stable MyBillboard refs (added once,
		// kept until removed), so per-ref keying works for them.
		// ConditionalWeakTable lets the GC reclaim entries when a persistent
		// billboard is removed.
		private static readonly ConditionalWeakTable<MyBillboard, FallbackOriginal> _fallbackOriginals =
			new ConditionalWeakTable<MyBillboard, FallbackOriginal>();

		// Squared proximity threshold (m²) used by the persistent-billboard
		// camera-anchored fallback. A Custom MyBillboard whose every emitted
		// vertex sits within sqrt(_cameraAnchoredThresholdSq) of the vanilla
		// camera position is treated as camera-anchored and re-baked into the
		// smoothed camera frame. 10 m comfortably covers HUD planes at
		// near-plane offset; world-anchored debug overlays farther out skip
		// the rebake and render at their vanilla world coords (already
		// correctly placed against the smoothed scene).
		private const double CameraAnchoredThresholdSq = 10.0 * 10.0;

		// Prev/curr cache for the per-ordinal lerp pass. Stores camera-local
		// offsets (L = (P_world - camera_vanilla) · inv(R_vanilla) at the
		// emission tick) rather than raw world positions, so the lerp output
		// and the fallback output share the projection formula
		// `P_new = camera_smoothed + L · R(smoothed)` — for static screen
		// offsets `L_prev == L_curr`, both paths produce identical output
		// regardless of camera rotation speed, and per-tick alternation
		// between them is invisible. For dynamic offsets the lerp transitions
		// L smoothly while the fallback locks to L_curr — different behavior
		// per path, same formula structure.
		//
		// Indexed by a *compacted* ordinal: the position of each
		// lerp-eligible billboard within the subsequence of lerp-eligible
		// entries in `MyRenderProxy.BillboardsRead`, not the raw entry index.
		// Orient-registered billboards (Build Info hairlines,
		// AddBillboardOriented quads, etc.) and non-default-parent/projection
		// billboards don't take cache slots, so third-party mods that emit
		// orient-registered content don't shift the HUD's cache index when
		// their per-tick count fluctuates. Logical-element identity is then
		// preserved across the HUD framework's own pool rotation (RHM cycles
		// 6 pools, Text HUD API cycles 4) as long as the UI tree's emission
		// order is stable.
		//
		// Sanity check (in TryRebakeOrdinalLerp): if `prev[i]` and `curr[i]`
		// disagree on fingerprint OR their L0 corners are further apart than
		// `OrdinalLerpMaxDistSq`, ordinal i shifted to a different logical
		// element this tick (UI tree changed, or a third-party mod inserted
		// non-orient Custom billboards) — fall back to L_curr instead of
		// lerping unrelated content together.
		private struct CachedPositions
		{
			public Vector3D L0;
			public Vector3D L1;
			public Vector3D L2;
			public Vector3D L3;
			public bool IsTriangle;

			// Fingerprint — stable per logical element across ticks (same
			// material, same blend, same parent/projection, same type). All
			// five must agree across `prev[i]` and `curr[i]` for the lerp to
			// fire; mismatches indicate ordinal-i shifted to a different
			// logical element this tick.
			public int MaterialId;
			public byte BlendType;
			public uint ParentId;
			public int CustomViewProjection;
		}

		private static CachedPositions[] _prevPositions = Array.Empty<CachedPositions>();
		private static int _prevCount;
		private static CachedPositions[] _currPositions = Array.Empty<CachedPositions>();
		private static int _currCount;
		private static long _ordinalCacheTickTimestamp;

		// Set on tick advance when `_currCount != _prevCount` — i.e. the
		// HUD framework's lerp-eligible billboard count grew or shrank
		// since last tick. When true, TryRebakeOrdinalLerp skips the lerp
		// branch and uses L_curr only (camera-anchored projection through
		// the smoothed view). Catches the hover/click case in RHM-based
		// menus where a highlight box or tooltip appears/disappears,
		// shifting every downstream ordinal by 1: the position-proximity
		// gate alone can't discriminate adjacent menu items (typical
		// item-to-item L distance is mm-scale at PixelToWorld near-plane
		// scaling, far below any threshold loose enough to allow
		// legitimate panel motion), so lerping `prev[i]` with `curr[i]`
		// after such a shift smoothly animates B's content into A's
		// previous screen position — the "swap chaos" symptom. Balanced
		// insert+delete (same count, ordinals still shifted) isn't
		// caught here; it falls through to the existing fingerprint +
		// L0-distance check below.
		private static bool _treeRestructured;

		// Position-proximity gate (m²) — companion to the fingerprint check.
		// Even when prev[i] and curr[i] agree on all fingerprint fields, if
		// their L0 corners are further apart than this they're probably
		// different logical elements that happen to share material/blend/etc.
		// — fall back instead of lerping. Tested on L0 only (L0..L3 of the
		// same billboard move together; one-corner test is enough). 0.5 m
		// radius comfortably exceeds per-tick HUD motion at walking speed
		// (camera moves ~5 cm/tick) and BV3 menu offset shifts during view
		// rotation (well under 50 cm/tick) without admitting distant aliases.
		private const double OrdinalLerpMaxDistSq = 0.5 * 0.5;

		// Per-render-frame rotation matrices for the L → world projection.
		// `_smoothedRotMat` rotates camera-local offsets into the
		// smoothed-camera world frame; `_invVanillaRotMat` rotates a world
		// offset into the vanilla camera's local frame (used by the cache
		// update step to compute fresh L from RHM-written world positions).
		// Both stay valid only while `_currentDeltaValid` is true.
		private static MatrixD _smoothedRotMat;
		private static MatrixD _invVanillaRotMat;

		// Smoothing alpha for the current render frame, copied from
		// RenderFrameSmoothing.LastSmoothingAlpha at the start of
		// RebakeBeforeGather. Stays at 0 when smoothing is disabled.
		private static float _currentAlpha;

		// Set on the sim thread by CameraCapture when the ControlledEntity is
		// not the local character (i.e. the camera is rigidly attached to a
		// piloted ship). RebakeLine reads it on the render thread to decide
		// whether line billboards should follow the smoothed camera (true) or
		// stay anchored to vanilla world coords (false). Volatile so the
		// render-thread read sees the latest sim-thread write.
		private static volatile bool _isPiloting;

		// Set on the sim thread by PatchEndCollectingInstanceData when the
		// block-placement preview produced render entities this tick AND the
		// gizmo isn't snapped to a grid. RebakeLine treats long-span lines as
		// camera-anchored in that case (the green/red placement-validity
		// wireframe is camera-driven via the gizmo matrix), applying the same
		// delta we apply to the preview's render entities so the box and the
		// ghost block stay co-located. Volatile for the render-thread read.
		private static volatile bool _isFreeFloatingPlacement;

		public static void SetPilotingState(bool piloting)
		{
			_isPiloting = piloting;
		}

		public static void SetFreeFloatingPlacement(bool active)
		{
			_isFreeFloatingPlacement = active;
		}

		// Called from a prefix on MyBillboardRenderer.Gather, on the render
		// thread, after our ProcessMessageQueue postfix has set the smoothed
		// camera matrices for this frame. Rewrites Position0…Position3 of any
		// Custom billboard whose orientation we captured at sim emission,
		// across BOTH the per-frame swap-read list AND the persistent list:
		//
		// - `MyRenderProxy.BillboardsRead` carries this frame's
		//   freshly-emitted billboards (including persistent ones, but only
		//   on the frame they spawn — they get dropped from the swap queue
		//   after).
		// - `MyRenderProxy.ApplyActionOnPersistentBillboards` walks the
		//   persistent list (`m_persistentBillboards`) which `PrepareList`
		//   also iterates each frame; this is the path that catches
		//   persistent billboards on every frame after spawn.
		public static void RebakeBeforeGather()
		{
			// When the user toggles smoothing off (Ctrl+F11),
			// `RenderFrameSmoothing.RunFrame` early-returns without updating
			// `LastSmoothedCameraPosition`, so the value retained here is
			// whatever was last computed when smoothing was on — stale by
			// potentially many frames. Rebaking quads against that stale
			// camera puts them at arbitrary screen positions (typically way
			// off-screen — the visible symptom is "billboards disappear when
			// turning smoothing off"). Skip the rebake entirely; vanilla's
			// freshly-emitted quads render at correct positions on their own.
			if (!Plugin.InterpolationEnabled)
			{
				return;
			}

			_currentSmoothedCameraPos = RenderFrameSmoothing.LastSmoothedCameraPosition;
			_currentAlpha = (float)RenderFrameSmoothing.LastSmoothingAlpha;

			// Vanilla pose for the persistent-billboard camera-anchored
			// fallback AND the L-cache projection matrices. Snapped in
			// RenderFrameSmoothing.SnapVanillaPose at the start of this render
			// frame, *not* re-read from CameraHistory here — a fresh read
			// would open a sim-tick race (sim could push a new tick during
			// ProcessMessageQueue, leaving the vanilla pose one tick newer
			// than BillboardsRead). See FrameVanillaCameraPosition's doc for
			// the race details.
			if (RenderFrameSmoothing.FrameVanillaValid)
			{
				_currentVanillaCameraPos = RenderFrameSmoothing.FrameVanillaCameraPosition;
				var deltaRot = RenderFrameSmoothing.LastSmoothedCameraRotation
					* Quaternion.Conjugate(RenderFrameSmoothing.FrameVanillaCameraRotation);
				_currentVanillaToSmoothedRot = Matrix.CreateFromQuaternion(deltaRot);
				_smoothedRotMat = Matrix.CreateFromQuaternion(RenderFrameSmoothing.LastSmoothedCameraRotation);
				_invVanillaRotMat = Matrix.CreateFromQuaternion(
					Quaternion.Conjugate(RenderFrameSmoothing.FrameVanillaCameraRotation));
				_currentDeltaValid = true;
			}
			else
			{
				_currentDeltaValid = false;
			}

			var billboards = MyRenderProxy.BillboardsRead;
			if (billboards != null && billboards.Count > 0)
			{
				// Compacted-ordinal lerp: `cacheIdx` advances only over
				// lerp-eligible billboards (Custom + default ParentID/CVP +
				// no orient registered). Orient-registered content (Build
				// Info edge highlights, face-highlight quads,
				// AddBillboardOriented users, etc.) doesn't increment
				// cacheIdx — so a third-party mod's per-tick count changes
				// don't shift the HUD billboards' ordinals.
				//
				// Tick advance does capture first, then rebake. The two-pass
				// shape lets `_currCount` settle before
				// `TryRebakeOrdinalLerp`'s `ordinal >= _currCount` guard
				// reads it; a previous single-pass version had the guard
				// reading the OLD count mid-loop, silently skipping the
				// rebake for new tail ordinals when the HUD framework grew
				// its emission count this tick. It also lets us compute
				// `_treeRestructured` before any lerp fires — required for
				// the gate to take effect on the same tick the tree
				// restructure happens, not one tick late.
				var tickAdvanced = RenderFrameSmoothing.FrameVanillaValid
					&& RenderFrameSmoothing.FrameVanillaTickTimestamp != _ordinalCacheTickTimestamp;

				if (tickAdvanced)
				{
					var swap = _prevPositions;
					_prevPositions = _currPositions;
					_currPositions = swap;
					_prevCount = _currCount;
					EnsureOrdinalCapacity(ref _currPositions, billboards.Count);

					var captureIdx = 0;
					for (var bbIdx = 0; bbIdx < billboards.Count; bbIdx++)
					{
						var bb = billboards[bbIdx];
						if (bb == null
							|| bb.LocalType != MyBillboard.LocalTypeEnum.Custom)
						{
							continue;
						}

						if (BillboardCapture.TryGetOrient(bb, out _))
						{
							continue;
						}

						if (bb.ParentID != uint.MaxValue
							|| bb.CustomViewProjection != -1)
						{
							continue;
						}

						CaptureLerpCacheEntry(bb, captureIdx);
						captureIdx++;
					}

					_currCount = captureIdx;
					_ordinalCacheTickTimestamp = RenderFrameSmoothing.FrameVanillaTickTimestamp;
					_treeRestructured = _currCount != _prevCount;
				}

				var rebakeIdx = 0;
				for (var bbIdx = 0; bbIdx < billboards.Count; bbIdx++)
				{
					var bb = billboards[bbIdx];
					if (bb == null
						|| bb.LocalType != MyBillboard.LocalTypeEnum.Custom)
					{
						continue;
					}

					if (TryRebakeWithOrient(bb))
					{
						continue;
					}

					if (bb.ParentID != uint.MaxValue
						|| bb.CustomViewProjection != -1)
					{
						continue;
					}

					TryRebakeOrdinalLerp(bb, rebakeIdx);
					rebakeIdx++;
				}
			}

			// Persistent billboards: per-ref camera-relative-transform fallback.
			// Persistent refs are stable across ticks (added once, kept until
			// removed), so the per-ref _fallbackOriginals cache behaves
			// correctly for them — no pool-rotation aliasing.
			MyRenderProxy.ApplyActionOnPersistentBillboards(_rebakeBillboard);
		}

		private static void EnsureOrdinalCapacity(ref CachedPositions[] arr, int cap)
		{
			if (arr.Length >= cap)
			{
				return;
			}
			var newSize = arr.Length == 0 ? Math.Max(256, cap) : Math.Max(cap, arr.Length * 2);
			Array.Resize(ref arr, newSize);
		}

		private static void RebakeOne(MyBillboard billboard)
		{
			if (billboard == null
				|| billboard.LocalType != MyBillboard.LocalTypeEnum.Custom)
			{
				return;
			}

			if (TryRebakeWithOrient(billboard))
			{
				return;
			}

			// No registered orientation — fall through to the
			// persistent-billboard camera-anchored fallback. Reached only via
			// the ApplyActionOnPersistentBillboards path;
			// BillboardsRead-iterated billboards take the per-ordinal lerp
			// inline in RebakeBeforeGather instead.
			TryRebakeCameraAnchoredFallback(billboard);
		}

		// Dispatches a billboard with a registered orient (Point/Line from
		// Add{Point,Line}Billboard prefixes, Direct from the
		// reverse-engineered single-emit AddBillboard postfix) to its
		// kind-specific rebake. Returns true when the billboard had an orient
		// and was handled (whether actually rebaked or skipped because of
		// CustomViewProjection != -1, which means it projects through a
		// separate camera in MyRenderProxy.BillboardsViewProjectionRead —
		// not smoothed here). Returns false when no orient is registered,
		// leaving the caller to choose its own no-orient fallback
		// (per-ordinal lerp for BillboardsRead, camera-anchored transform for
		// persistent).
		private static bool TryRebakeWithOrient(MyBillboard billboard)
		{
			if (!BillboardCapture.TryGetOrient(billboard, out var orient))
			{
				return false;
			}

			if (orient.CustomViewProjection != -1)
			{
				return true;
			}

			switch (orient.Kind)
			{
				case BillboardKind.Point:
					RebakeAxisAligned(billboard, orient, _currentSmoothedCameraPos);
					break;
				case BillboardKind.Line:
					RebakeLine(billboard, orient, _currentSmoothedCameraPos);
					break;
				case BillboardKind.Direct:
					RebakeDirect(billboard, orient, _currentSmoothedCameraPos);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(orient.Kind),
						orient.Kind, "SmoothFrames: unknown BillboardKind");
			}
			return true;
		}

		// Computes camera-local L vectors from the billboard's RHM-written
		// world positions and stores them at `cacheIdx` in `_currPositions`
		// for later lerp. Called once per lerp-eligible billboard per sim
		// tick (when `tickAdvanced` is true); the cache is reused across all
		// render frames within the tick. See `_prevPositions`'s doc comment
		// for why the cache is keyed by a compacted ordinal rather than the
		// raw `BillboardsRead` index.
		private static void CaptureLerpCacheEntry(MyBillboard bb, int cacheIdx)
		{
			// L = (P_world - camera_vanilla) · inv(R_vanilla): camera-local
			// offset of P at the emission tick.
			var off0 = bb.Position0 - _currentVanillaCameraPos;
			var off1 = bb.Position1 - _currentVanillaCameraPos;
			var off2 = bb.Position2 - _currentVanillaCameraPos;
			Vector3D.TransformNormal(ref off0, ref _invVanillaRotMat, out _currPositions[cacheIdx].L0);
			Vector3D.TransformNormal(ref off1, ref _invVanillaRotMat, out _currPositions[cacheIdx].L1);
			Vector3D.TransformNormal(ref off2, ref _invVanillaRotMat, out _currPositions[cacheIdx].L2);

			var isTri = bb is MyTriangleBillboard;
			_currPositions[cacheIdx].IsTriangle = isTri;
			if (isTri)
			{
				_currPositions[cacheIdx].L3 = default;
			}
			else
			{
				var off3 = bb.Position3 - _currentVanillaCameraPos;
				Vector3D.TransformNormal(ref off3, ref _invVanillaRotMat, out _currPositions[cacheIdx].L3);
			}

			_currPositions[cacheIdx].MaterialId = bb.Material.Id;
			_currPositions[cacheIdx].BlendType = (byte)bb.BlendType;
			_currPositions[cacheIdx].ParentId = bb.ParentID;
			_currPositions[cacheIdx].CustomViewProjection = bb.CustomViewProjection;
		}

		// Rebake the i-th lerp-eligible billboard as
		// `P_new = camera_smoothed + L · R(smoothed)`
		// where L is either lerp(L_prev, L_curr, α) (sanity passes) or just
		// L_curr (sanity fails or no prev). The two paths share the formula,
		// so for static screen offsets (L_prev == L_curr) they produce
		// identical output regardless of camera rotation speed — per-tick
		// alternation between them is invisible. For dynamic offsets (BV3
		// menu re-projecting the targeted block, HUD Compass labels sliding
		// to track heading), the lerp transitions L smoothly while the
		// fallback locks to L_curr; the difference is a small per-tick snap
		// on dynamic content, not flicker.
		//
		// Sanity check: prev/curr fingerprints must agree, and their
		// camera-local offsets must be within `OrdinalLerpMaxDistSq` of
		// each other.
		// Mismatches mean ordinal i shifted to a different logical element
		// this tick (Build Info or another mod inserted/removed billboards
		// mid-list) — the fallback locks to L_curr in that case.
		//
		// Untouched in non-Custom and non-default-parent/projection cases —
		// the fallback path proper for those is the lerp loop itself
		// returning without modification, so the engine renders the
		// billboard's vanilla position through the smoothed view.
		private static void TryRebakeOrdinalLerp(MyBillboard billboard, int ordinal)
		{
			if (!_currentDeltaValid
				|| billboard.ParentID != uint.MaxValue
				|| billboard.CustomViewProjection != -1)
			{
				return;
			}

			if (ordinal >= _currCount)
			{
				return;
			}

			var curr = _currPositions[ordinal];
			if (curr.MaterialId == -1)
			{
				return;
			}

			// Filter out world-anchored Custom billboards: their L magnitude
			// grows with their world distance from the camera, while HUD
			// content sits at near-plane offsets (well under 10 m). Without
			// this gate the smoothed re-projection would drag distant world
			// quads to follow the camera (incorrect — they should stay put
			// while the smoothed view alone makes them slide on screen).
			if (curr.L0.LengthSquared() > CameraAnchoredThresholdSq
				|| curr.L1.LengthSquared() > CameraAnchoredThresholdSq
				|| curr.L2.LengthSquared() > CameraAnchoredThresholdSq)
			{
				return;
			}
			if (!curr.IsTriangle && curr.L3.LengthSquared() > CameraAnchoredThresholdSq)
			{
				return;
			}

			Vector3D L0 = curr.L0, L1 = curr.L1, L2 = curr.L2, L3 = curr.L3;

			// `_treeRestructured` gates the lerp branch when the lerp-
			// eligible count changed across the last tick advance:
			// ordinals beyond the insertion/deletion point now reference a
			// different logical element than they did in `_prevPositions`,
			// and the fingerprint + distance check below can't catch the
			// shift when adjacent items share material/blend/CVP and sit
			// within mm of each other in camera-local L (typical of
			// closely-packed RHM/BV3 menu lists). Falling back to L_curr
			// for the whole tick produces a 1-tick snap on legitimately
			// moving panels but eliminates the swap chaos symptom (B's
			// content smoothly animating in from A's previous slot when
			// the UI tree restructures on hover/click).
			if (!_treeRestructured && ordinal < _prevCount)
			{
				var prev = _prevPositions[ordinal];
				if (prev.MaterialId == curr.MaterialId
					&& prev.BlendType == curr.BlendType
					&& prev.ParentId == curr.ParentId
					&& prev.CustomViewProjection == curr.CustomViewProjection
					&& prev.IsTriangle == curr.IsTriangle
					&& Vector3D.DistanceSquared(prev.L0, curr.L0) <= OrdinalLerpMaxDistSq)
				{
					Vector3D.Lerp(ref prev.L0, ref curr.L0, _currentAlpha, out L0);
					Vector3D.Lerp(ref prev.L1, ref curr.L1, _currentAlpha, out L1);
					Vector3D.Lerp(ref prev.L2, ref curr.L2, _currentAlpha, out L2);
					if (!curr.IsTriangle)
					{
						Vector3D.Lerp(ref prev.L3, ref curr.L3, _currentAlpha, out L3);
					}
				}
			}

			Vector3D.TransformNormal(ref L0, ref _smoothedRotMat, out var rot0);
			billboard.Position0 = _currentSmoothedCameraPos + rot0;
			Vector3D.TransformNormal(ref L1, ref _smoothedRotMat, out var rot1);
			billboard.Position1 = _currentSmoothedCameraPos + rot1;
			Vector3D.TransformNormal(ref L2, ref _smoothedRotMat, out var rot2);
			billboard.Position2 = _currentSmoothedCameraPos + rot2;
			if (!curr.IsTriangle)
			{
				Vector3D.TransformNormal(ref L3, ref _smoothedRotMat, out var rot3);
				billboard.Position3 = _currentSmoothedCameraPos + rot3;
			}
		}

		// Apply a translation+rotation delta to each emitted vertex of a
		// Custom MyBillboard so it lands at the smoothed-camera-frame
		// equivalent of where it was baked in the vanilla-camera frame. Same
		// vertex formula as RebakeDirect, but driven from the per-frame
		// vanilla camera pose (no per-billboard orient capture needed). Quad
		// vs. triangle billboards are discriminated by runtime type —
		// MyTriangleBillboard only uses Position0..2 and leaves Position3 at
		// its allocation default, so writing it would produce a stale corner
		// the renderer happens to ignore for the triangle path but would
		// skew the proximity gate.
		private static void TryRebakeCameraAnchoredFallback(MyBillboard billboard)
		{
			if (!_currentDeltaValid
				|| billboard.ParentID != uint.MaxValue
				|| billboard.CustomViewProjection != -1)
			{
				return;
			}

			// Re-capture vanilla into the entry on cache miss OR when the entry
			// is from a previous sim tick (RHM has just rewritten positions
			// and the cached values are stale). The destructive transform
			// below treats orig.P0..3 as the source-of-truth vanilla pose, so
			// we only ever want to read from a freshly-captured entry.
			// Mutating the existing instance in place avoids a
			// per-billboard-per-tick allocation.
			var hasOrig = _fallbackOriginals.TryGetValue(billboard, out var orig);
			if (!hasOrig || orig.TickTimestamp != RenderFrameSmoothing.FrameVanillaTickTimestamp)
			{
				var p0 = billboard.Position0;
				var p1 = billboard.Position1;
				var p2 = billboard.Position2;

				if (Vector3D.DistanceSquared(p0, _currentVanillaCameraPos) > CameraAnchoredThresholdSq
					|| Vector3D.DistanceSquared(p1, _currentVanillaCameraPos) > CameraAnchoredThresholdSq
					|| Vector3D.DistanceSquared(p2, _currentVanillaCameraPos) > CameraAnchoredThresholdSq)
				{
					return;
				}

				var isTriangle = billboard is MyTriangleBillboard;
				var p3 = isTriangle ? default : billboard.Position3;
				if (!isTriangle
					&& Vector3D.DistanceSquared(p3, _currentVanillaCameraPos) > CameraAnchoredThresholdSq)
				{
					return;
				}

				if (!hasOrig)
				{
					orig = new FallbackOriginal();
					_fallbackOriginals.Add(billboard, orig);
				}

				orig.P0 = p0;
				orig.P1 = p1;
				orig.P2 = p2;
				orig.P3 = p3;
				orig.IsTriangle = isTriangle;
				orig.TickTimestamp = RenderFrameSmoothing.FrameVanillaTickTimestamp;
			}

			billboard.Position0 = TransformCornerCameraRelative(
				orig.P0, _currentVanillaCameraPos, _currentSmoothedCameraPos, ref _currentVanillaToSmoothedRot);
			billboard.Position1 = TransformCornerCameraRelative(
				orig.P1, _currentVanillaCameraPos, _currentSmoothedCameraPos, ref _currentVanillaToSmoothedRot);
			billboard.Position2 = TransformCornerCameraRelative(
				orig.P2, _currentVanillaCameraPos, _currentSmoothedCameraPos, ref _currentVanillaToSmoothedRot);

			if (!orig.IsTriangle)
			{
				billboard.Position3 = TransformCornerCameraRelative(
					orig.P3, _currentVanillaCameraPos, _currentSmoothedCameraPos, ref _currentVanillaToSmoothedRot);
			}
		}

		private static void RebakeAxisAligned(MyBillboard billboard, BillboardOrient orient, Vector3D smoothedCameraPos)
		{
			// Re-anchor against the smoothed grid pose if origin lies on a
			// moving grid (e.g. Build Info CoM marker on a coasting ship).
			// Without this the point quad sits at the grid's vanilla world
			// position while the grid's mesh renders at its smoothed pose.
			var origin = orient.Origin;
			if (BillboardCorrections.TryFindCorrection(origin, out var movingGridCorrection))
			{
				Vector3D.Transform(ref origin, ref movingGridCorrection, out var corrected);
				origin = corrected;
			}

			if (!MyUtils.GetBillboardQuadAdvancedRotated(out var quad, origin,
				orient.SizeX, orient.SizeY, orient.Angle, smoothedCameraPos))
			{
				return;
			}

			ApplyQuadToBillboard(billboard, ref quad, ref orient.WorldToLocal, orient.ParentId);
		}

		private static void RebakeDirect(MyBillboard billboard, BillboardOrient orient, Vector3D smoothedCameraPos)
		{
			// Make each captured corner camera-relative: take its offset from
			// the vanilla camera, rotate by the camera's vanilla→smoothed
			// rotation delta, and place it relative to the smoothed camera.
			//
			//   new_corner = smoothed_pos + delta_rot · (vanilla_corner − vanilla_pos)
			//
			// For pure translation this collapses to corner + (smoothed_pos −
			// vanilla_pos); for pure rotation it pivots the quad around the
			// camera. Right for player-anchored billboards (HUD overlays
			// re-emitted each tick at player.pos + R(player) · local_offset),
			// because their origin moves with both the player's position and
			// orientation between sim ticks.
			var deltaRot = RenderFrameSmoothing.LastSmoothedCameraRotation
				* Quaternion.Conjugate(orient.VanillaCameraRotation);
			var deltaRotMat = Matrix.CreateFromQuaternion(deltaRot);
			MatrixD deltaRotMatD = deltaRotMat;

			billboard.Position0 = TransformCornerCameraRelative(
				orient.DirectP0, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);
			billboard.Position1 = TransformCornerCameraRelative(
				orient.DirectP1, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);
			billboard.Position2 = TransformCornerCameraRelative(
				orient.DirectP2, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);
			billboard.Position3 = TransformCornerCameraRelative(
				orient.DirectP3, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);
		}

		private static Vector3D TransformCornerCameraRelative(Vector3D vanillaCorner,
			Vector3D vanillaCameraPos, Vector3D smoothedCameraPos, ref MatrixD deltaRot)
		{
			var offset = vanillaCorner - vanillaCameraPos;
			Vector3D.TransformNormal(ref offset, ref deltaRot, out var rotatedOffset);
			return smoothedCameraPos + rotatedOffset;
		}

		// Build Info's overlay system pulls "see-through-walls" line endpoints
		// toward the camera by ~1% (a depth-test trick that draws them in
		// front of geometry), giving them a tiny world-space span. Solid
		// edges retain the actual block size (~1 m). Discriminating by span²
		// (rather than distance-from-camera, which fails when you select the
		// cockpit you're sitting in) cleanly splits the two cases on foot,
		// where camera-pulled lines need a translation correction the vanilla
		// rebake doesn't handle.
		private const double CameraPulledSpanSq = 0.1 * 0.1;
		private const double DepthPullScale = 0.01;
		private const double DepthPullInverseScale = 1.0 / DepthPullScale;
		private const double DepthPullComplement = 1.0 - DepthPullScale;

		private static void RebakeLine(MyBillboard billboard, BillboardOrient orient, Vector3D smoothedCameraPos)
		{
			// While piloting, the camera moves with the ship grid each sim
			// tick, so lines drawn on that grid (highlights from inside the
			// cockpit, selection boxes, etc.) need to follow the smoothed
			// grid. Both solid and depth-pulled lines on the piloted ship
			// follow rigidly with the camera, so the correct translation is
			// the full (smoothed − vanilla) delta — we don't apply the
			// depth-pull complement in this branch.
			if (_isPiloting)
			{
				var polyLine = default(MyPolyLineD);
				polyLine.LineDirectionNormalized = orient.Direction;
				polyLine.Point0 = orient.Origin;
				polyLine.Point1 = orient.Origin + orient.Direction * orient.Length;
				polyLine.Thickness = orient.Thickness;

				if (Vector3D.IsZero(smoothedCameraPos - polyLine.Point0, 1E-06))
				{
					return;
				}

				MyUtils.GetPolyLineQuad(out var quad, ref polyLine, smoothedCameraPos);

				var delta = smoothedCameraPos - orient.VanillaCameraPos;
				quad.Point0 += delta;
				quad.Point1 += delta;
				quad.Point2 += delta;
				quad.Point3 += delta;

				ApplyQuadToBillboard(billboard, ref quad, ref orient.WorldToLocal, orient.ParentId);
				return;
			}

			// On foot: solid lines (long span) are anchored to a static world
			// position; the engine's vanilla quad through our smoothed view
			// matrix renders at the same screen position as the underlying
			// mesh, so we leave them alone. Camera-pulled lines (short span —
			// Build Info's depth-pulled overlay box) sit at camPos + (target
			// − camPos) × 0.01 in world space; without correction they stay
			// anchored to the sim-tick camera's position while the rest of
			// the scene moves with the smoothed camera, producing the wobble
			// proportional to walking speed. Translating their endpoints by
			// (smoothed − vanilla) × (1 − 0.01) puts them at the
			// smoothed-camera equivalent position.
			var spanVec = (Vector3D)orient.Direction * orient.Length;
			if (spanVec.LengthSquared() >= CameraPulledSpanSq)
			{
				// Long span on foot. The "world-anchored" assumption holds for
				// selection boxes / terminal underlays anchored to a static
				// block face — the engine's vanilla quad through our smoothed
				// view matrix renders correctly with no help. Two cases need
				// explicit re-anchoring:
				//
				// 1. Free-floating placement: the green/red wireframe around
				//    the ghost block is camera-anchored
				//    (origin = cam + distance·forward), not world-anchored.
				//    Apply the camera vanilla→smoothed transform via
				//    RebakeLineCameraAnchored. Checked FIRST because the
				//    ghost-box origin can sit inside a nearby grid's bounding
				//    sphere (player standing on / next to a ship) and falsely
				//    match the moving-grid lookup below — routing a
				//    camera-anchored line through a grid pose delta wobbles
				//    the box against the smoothed camera.
				//
				// 2. Origin sits inside a moving grid's vanilla bounding
				//    sphere (jetpack-on-moving-ship: Build Info block-edge
				//    highlights on a coasting ship). The grid's mesh renders
				//    at its smoothed pose, but the line endpoints are still
				//    at the vanilla pose — apply the grid's rigid correction
				//    matrix to weld the line to the smoothed mesh. Handles
				//    both translation and rotation.
				if (_isFreeFloatingPlacement)
				{
					RebakeLineCameraAnchored(billboard, orient, smoothedCameraPos);
					return;
				}

				if (BillboardCorrections.TryFindCorrection(orient.Origin, out var movingGridCorrection))
				{
					RebakeLineWithCorrection(billboard, orient, ref movingGridCorrection, smoothedCameraPos);
				}
				return;
			}

			// Short-span depth-pulled lines on a moving grid (Build Info's
			// see-through-walls overlay while jetpacking past a coasting
			// ship).
			// The static-target correction below is incomplete here: it
			// translates each endpoint by (smoothedCam − vanillaCam) × 0.99,
			// which is right only when the actual block face stays at its
			// vanilla world position. On a moving ship the block face moves
			// with the grid's smoothing delta between sim ticks, leaving a
			// residual wobble of ship_motion × 0.01 on the hairlines (small,
			// but visible against the smoothed mesh). Recover the depth-pulled
			// endpoints' true target points, apply the grid's vanilla→smoothed
			// correction to those targets, then re-depth-pull against the
			// smoothed camera.
			var origin = orient.Origin;
			var endpoint = origin + (Vector3D)orient.Direction * orient.Length;
			var vanillaCam = orient.VanillaCameraPos;
			var vanillaTargetOrigin = vanillaCam + (origin - vanillaCam) * DepthPullInverseScale;

			if (BillboardCorrections.TryFindCorrection(vanillaTargetOrigin, out var pulledGridCorrection))
			{
				var vanillaTargetEnd = vanillaCam + (endpoint - vanillaCam) * DepthPullInverseScale;
				RebakeLineDepthPulledWithCorrection(billboard, orient, ref pulledGridCorrection,
					vanillaTargetOrigin, vanillaTargetEnd, smoothedCameraPos);
				return;
			}

			var pullDelta = (smoothedCameraPos - vanillaCam) * DepthPullComplement;
			var pulledOrigin = origin + pullDelta;
			var pulledEnd = pulledOrigin + (Vector3D)orient.Direction * orient.Length;

			if (Vector3D.IsZero(smoothedCameraPos - pulledOrigin, 1E-06))
			{
				return;
			}

			var pulledLine = default(MyPolyLineD);
			pulledLine.LineDirectionNormalized = orient.Direction;
			pulledLine.Point0 = pulledOrigin;
			pulledLine.Point1 = pulledEnd;
			pulledLine.Thickness = orient.Thickness;

			MyUtils.GetPolyLineQuad(out var pulledQuad, ref pulledLine, smoothedCameraPos);

			ApplyQuadToBillboard(billboard, ref pulledQuad, ref orient.WorldToLocal, orient.ParentId);
		}

		// Rebuild a depth-pulled line whose true target endpoints sit on a
		// moving grid. Apply the grid's rigid pose correction (vanilla →
		// smoothed) to both true target points, then re-depth-pull each
		// against the smoothed camera by the same 0.01 factor the
		// sim-emission used. End result: hairlines stay welded to the
		// smoothed block face on a coasting ship, with no residual
		// ship-motion wobble.
		private static void RebakeLineDepthPulledWithCorrection(MyBillboard billboard, BillboardOrient orient,
			ref MatrixD correction, Vector3D vanillaTargetOrigin, Vector3D vanillaTargetEnd,
			Vector3D smoothedCameraPos)
		{
			Vector3D.Transform(ref vanillaTargetOrigin, ref correction, out var smoothTargetOrigin);
			Vector3D.Transform(ref vanillaTargetEnd, ref correction, out var smoothTargetEnd);

			var pulledOrigin = smoothedCameraPos + (smoothTargetOrigin - smoothedCameraPos) * DepthPullScale;
			var pulledEnd = smoothedCameraPos + (smoothTargetEnd - smoothedCameraPos) * DepthPullScale;

			if (Vector3D.IsZero(smoothedCameraPos - pulledOrigin, 1E-06))
			{
				return;
			}

			var newDir = pulledEnd - pulledOrigin;
			var newLen = newDir.Length();
			if (newLen < 1E-06)
			{
				return;
			}

			var polyLine = default(MyPolyLineD);
			polyLine.LineDirectionNormalized = newDir / newLen;
			polyLine.Point0 = pulledOrigin;
			polyLine.Point1 = pulledEnd;
			polyLine.Thickness = orient.Thickness;

			MyUtils.GetPolyLineQuad(out var quad, ref polyLine, smoothedCameraPos);

			ApplyQuadToBillboard(billboard, ref quad, ref orient.WorldToLocal, orient.ParentId);
		}

		// Apply a rigid correction matrix (vanilla → smoothed pose) to both
		// endpoints of a line, then rebuild the polyline quad facing the
		// smoothed camera. Used for long-span lines whose origin lies on a
		// moving grid: the correction is the grid's inv(curr_world) *
		// smoothed_world, so the transform takes a world point at the vanilla
		// grid pose to the corresponding world point at the smoothed grid
		// pose — handles ship translation AND rotation between sim ticks.
		private static void RebakeLineWithCorrection(MyBillboard billboard,
			BillboardOrient orient, ref MatrixD correction,
			Vector3D smoothedCameraPos)
		{
			var origVanilla = orient.Origin;
			var endpointVanilla = origVanilla + (Vector3D)orient.Direction * orient.Length;

			Vector3D.Transform(ref origVanilla, ref correction, out var origSmoothed);
			Vector3D.Transform(ref endpointVanilla, ref correction, out var endpointSmoothed);

			if (Vector3D.IsZero(smoothedCameraPos - origSmoothed, 1E-06))
			{
				return;
			}

			var newDirVec = endpointSmoothed - origSmoothed;
			var newLen = newDirVec.Length();
			if (newLen < 1E-06)
			{
				return;
			}

			var polyLine = default(MyPolyLineD);
			polyLine.LineDirectionNormalized = newDirVec / newLen;
			polyLine.Point0 = origSmoothed;
			polyLine.Point1 = endpointSmoothed;
			polyLine.Thickness = orient.Thickness;

			MyUtils.GetPolyLineQuad(out var quad, ref polyLine, smoothedCameraPos);

			ApplyQuadToBillboard(billboard, ref quad, ref orient.WorldToLocal, orient.ParentId);
		}

		// Free-floating placement gizmo lines are camera-anchored: their
		// endpoints depend on both the camera position (the gizmo sits a
		// raycast distance ahead of the player) and the camera rotation (the
		// gizmo orientation rotates with view direction). Apply the full
		// vanilla→smoothed transform — same camera-relative formula as
		// RebakeDirect for HUD billboards — to each endpoint, then re-build
		// the polyline quad facing the smoothed camera.
		private static void RebakeLineCameraAnchored(MyBillboard billboard,
			BillboardOrient orient, Vector3D smoothedCameraPos)
		{
			var deltaRot = RenderFrameSmoothing.LastSmoothedCameraRotation
				* Quaternion.Conjugate(orient.VanillaCameraRotation);
			var deltaRotMat = Matrix.CreateFromQuaternion(deltaRot);
			MatrixD deltaRotMatD = deltaRotMat;

			var origVanilla = orient.Origin;
			var endpointVanilla = origVanilla + (Vector3D)orient.Direction * orient.Length;

			var origSmoothed = TransformCornerCameraRelative(
				origVanilla, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);
			var endpointSmoothed = TransformCornerCameraRelative(
				endpointVanilla, orient.VanillaCameraPos, smoothedCameraPos, ref deltaRotMatD);

			if (Vector3D.IsZero(smoothedCameraPos - origSmoothed, 1E-06))
			{
				return;
			}

			var newDirVec = endpointSmoothed - origSmoothed;
			var newLen = newDirVec.Length();
			if (newLen < 1E-06)
			{
				return;
			}

			var polyLine = default(MyPolyLineD);
			polyLine.LineDirectionNormalized = newDirVec / newLen;
			polyLine.Point0 = origSmoothed;
			polyLine.Point1 = endpointSmoothed;
			polyLine.Thickness = orient.Thickness;

			MyUtils.GetPolyLineQuad(out var quad, ref polyLine, smoothedCameraPos);

			ApplyQuadToBillboard(billboard, ref quad, ref orient.WorldToLocal, orient.ParentId);
		}

		private static void ApplyQuadToBillboard(MyBillboard billboard, ref MyQuadD quad, ref MatrixD worldToLocal,
			uint parentId)
		{
			billboard.Position0 = quad.Point0;
			billboard.Position1 = quad.Point1;
			billboard.Position2 = quad.Point2;
			billboard.Position3 = quad.Point3;

			// AddX methods transform corners into the parent's local frame
			// when ParentID is set; reproduce that here so the renderer's
			// world-from-parent multiplication lines up.
			if (parentId == uint.MaxValue)
			{
				return;
			}

			Vector3D.Transform(ref billboard.Position0, ref worldToLocal, out billboard.Position0);
			Vector3D.Transform(ref billboard.Position1, ref worldToLocal, out billboard.Position1);
			Vector3D.Transform(ref billboard.Position2, ref worldToLocal, out billboard.Position2);
			Vector3D.Transform(ref billboard.Position3, ref worldToLocal, out billboard.Position3);
		}
	}

	// Vanilla Position0..3 captured by the camera-anchored fallback. Same
	// role as BillboardOrient.DirectP0..3 — preserves the vanilla pose so
	// each render frame's rebake reads from a stable input instead of the
	// previous frame's already-transformed output. Each entry carries the
	// sim tick its positions were captured against; on tick advance the
	// fallback re-captures into the same instance in place. Triangle
	// billboards leave P3 unused (RHM only writes Position0..2).
	internal sealed class FallbackOriginal
	{
		public long TickTimestamp;
		public Vector3D P0;
		public Vector3D P1;
		public Vector3D P2;
		public Vector3D P3;
		public bool IsTriangle;
	}

	/// <summary>
	///     Render-thread prefix on <c>MyBillboardRenderer.Gather</c>. Runs
	///     once per render frame after our smoothed-camera matrices are in
	///     place, before the engine builds vertex/index buffers — re-baking
	///     the quad positions in place is the only mutation needed.
	///
	///     <see cref="HarmonyBefore"/> declares ordering against the
	///     Prism.RenderPerf plugin, which installs its own Prefix on the same
	///     method that returns <c>false</c> and reads
	///     <see cref="MyBillboard.Position0"/>..<see cref="MyBillboard.Position3"/>
	///     directly to upload them to GPU instance buffers (skipping vanilla
	///     <c>GatherInternal</c>). Without this hint Harmony orders the two
	///     equal-priority prefixes implementation-defined, and on the runs
	///     where RenderPerf goes first the Custom billboards we'd have
	///     rebaked get uploaded with sim-tick-stale corner positions —
	///     visible ghosting on Build Info edges, antenna lines, GPS marker
	///     pins, and the Direct HUD billboards (Text HUD API, HUD Compass)
	///     at high refresh during fast camera motion. The annotation is a
	///     safe no-op when RenderPerf isn't loaded.
	/// </summary>
	[HarmonyPatch]
	[HarmonyBefore("Prism.RenderPerf.Plugin")]
	public static class PatchBillboardGather
	{
		public static MethodBase TargetMethod()
		{
			// MyBillboardRenderer is internal; resolved by name. Namespace is
			// VRageRender (the broad legacy namespace), not VRage.Render11.
			return AccessTools.Method("VRageRender.MyBillboardRenderer:Gather")
				?? throw Errors.NotResolved("VRageRender.MyBillboardRenderer.Gather");
		}

		public static void Prefix()
		{
			BillboardSmoothing.RebakeBeforeGather();
		}
	}
}
