using System;
using VRageMath;

namespace SmoothFrames
{
	/// <summary>
	///     Per-render-frame store of vanilla→smoothed rigid corrections for
	///     moving grids (sphere-keyed) and the local player's piloted ship
	///     (direct slot). Populated by <c>RenderFrameSmoothing.RunFrame</c>
	///     when each captured grid is interpolated; consumed by the billboard
	///     rebake hot path (line and point quads on a coasting ship) and by
	///     HUD-marker / artificial-horizon code that needs to project
	///     ship-anchored world coords through the smoothed grid pose.
	///
	///     Split from <see cref="BillboardSmoothing"/> so the four files that
	///     reach in here (<c>RenderFrameSmoothing</c>,
	///     <c>HudMarkerSmoothing</c>, <c>ArtificialHorizonSmoothing</c>, and
	///     the rebake engine itself) reference a class whose name describes
	///     what they're fetching.
	/// </summary>
	internal static class BillboardCorrections
	{
		// Per-render-frame list of moving grids whose vanilla bounding sphere
		// contains an emitted billboard's origin. Populated by
		// RenderFrameSmoothing.RunFrame on the render thread, consumed (also
		// on the render thread) by the billboard rebake engine via
		// TryFindCorrection. Single-threaded access pattern, so no
		// synchronization needed.
		//
		// Solves the jetpack-on-moving-ship case: Build Info emits block-edge
		// highlights as line billboards in world coordinates derived from the
		// grid's *vanilla* (sim-tick) WorldMatrix. Our entity-smoothing path
		// re-poses the grid's render objects to its smoothed pose, so the mesh
		// renders at the smoothed pose while the billboards stay at the
		// vanilla pose — visible wobble proportional to ship velocity. The
		// piloting branch in RebakeLine masks this when the camera follows the
		// ship rigidly, but jetpack-on-foot has no such follow, so long-span
		// lines need explicit re-anchoring against the grid's smoothed pose.
		private static MovingGridCorrection[] _movingGrids = Array.Empty<MovingGridCorrection>();
		private static int _movingGridsCount;

		// Current sim-tick correction for the local player's piloted ship
		// grid: `inv(grid_T) * grid_smoothed`. Populated per render frame
		// when the grid flagged `IsPilotedShip` is processed; consumed by
		// HudMarkerSmoothing's crosshair path so it can route the crosshair
		// anchor (1000 m forward of the seat — outside any grid sphere, so
		// the moving-grid sphere lookup wouldn't hit it) through the smoothed
		// ship pose. Null when not piloting. Reset to null at the start of
		// each render frame in BeginFrame.
		private static MatrixD? _pilotedShipCorrection;

		// Build Info's overlay inflates each block's local AABB by 0.1 m (large
		// grids) / 0.03 m (small grids) before computing edge corners for the
		// see-through-walls hairlines. The inflated corner of the block at
		// the very edge of the grid's AABB can sit up to 0.1 × √3 ≈ 0.173 m
		// outside the grid's WorldVolume sphere (which circumscribes the
		// un-inflated WorldAABB). Add a safety margin to the registered sphere
		// so the containment lookup catches those targets — without it the
		// depth-pulled hairlines on a corner block consistently fail the test
		// and fall through to the camera-delta path (~ vanilla position with
		// residual ship-motion wobble). 0.3 m covers the large-block inflation
		// comfortably; the false-positive surface (a non-grid line within
		// 30 cm of the grid sphere) is small in practice.
		private const double GridSpherePadMeters = 0.3;

		// Called once at the start of each render frame from
		// RenderFrameSmoothing. Postfix, before Register is called for any
		// grid. Resets the list without freeing the backing array (it'll grow
		// to a stable size after a few frames in any given world).
		public static void BeginFrame()
		{
			_movingGridsCount = 0;
			_pilotedShipCorrection = null;
		}

		// Called once per moving grid per render frame. Stores the vanilla
		// bounding-sphere check (cheap point-in-sphere) plus two precomputed
		// rigid correction matrices that map a world point from one of the
		// grid's sim-tick poses onto its smoothed-grid-pose equivalent.
		// `correction` uses the current sim-tick pose as the reference
		// (`inv(curr_world) * smoothed_world`) — the shape line/point
		// billboards need. `prevPoseCorrection` uses the previous sim-tick
		// pose as the reference (`inv(prev_world) * smoothed_world`) — the
		// shape HUD markers need because their POI WorldPositions lag by one
		// sim tick (see the field doc on MovingGridCorrection).
		public static void Register(
			BoundingSphereD volume,
			ref MatrixD correction,
			ref MatrixD prevPoseCorrection)
		{
			if (_movingGrids.Length <= _movingGridsCount)
			{
				Array.Resize(ref _movingGrids, Math.Max(4, _movingGrids.Length * 2));
			}

			_movingGrids[_movingGridsCount].VolumeCenter = volume.Center;
			var paddedRadius = volume.Radius + GridSpherePadMeters;
			_movingGrids[_movingGridsCount].VolumeRadiusSq = paddedRadius * paddedRadius;
			_movingGrids[_movingGridsCount].Correction = correction;
			_movingGrids[_movingGridsCount].PrevPoseCorrection = prevPoseCorrection;
			_movingGridsCount++;
		}

		// Stash the piloted ship grid's current-pose correction for direct
		// lookup. Called from RenderFrameSmoothing once per render frame
		// when the grid flagged `IsPilotedShip` is registered. Replaces,
		// not adds — there's only ever one piloted ship.
		public static void SetPilotedShipCorrection(ref MatrixD correction)
		{
			_pilotedShipCorrection = correction;
		}

		// True when the piloted ship's correction was stashed this render
		// frame. Consumed by the HUD crosshair path to map its anchor
		// (`seat_pos + 1000 * seat_forward`) onto the smoothed ship pose
		// without needing a sphere containment test (the anchor is far
		// outside any grid's bounding sphere), and by the artificial-horizon
		// path to advance the cockpit's sim-T world matrix to the
		// smoothed-grid pose for the gravity dot products.
		public static bool TryGetPilotedShipCorrection(out MatrixD correction)
		{
			if (_pilotedShipCorrection.HasValue)
			{
				correction = _pilotedShipCorrection.Value;
				return true;
			}
			correction = MatrixD.Identity;
			return false;
		}

		// True when origin lies inside a registered grid's vanilla bounding
		// sphere; out-param receives that grid's correction matrix. First-hit
		// — overlapping spheres on multiple grids would resolve to whichever
		// got registered first, which in practice means whichever the
		// sim-thread collector visited first. Build Info doesn't highlight
		// blocks across two stacked grids in the same call, so the ambiguity
		// is hypothetical.
		public static bool TryFindCorrection(Vector3D origin, out MatrixD correction)
		{
			for (var i = 0; i < _movingGridsCount; i++)
			{
				var distSq = Vector3D.DistanceSquared(origin, _movingGrids[i].VolumeCenter);
				if (distSq > _movingGrids[i].VolumeRadiusSq)
				{
					continue;
				}

				correction = _movingGrids[i].Correction;
				return true;
			}

			correction = MatrixD.Identity;
			return false;
		}

		// Same containment test as TryFindCorrection but returns the
		// prev-pose-based correction (`inv(prev_world) * smoothed_world`) —
		// what HUD markers need because their POI WorldPosition is one sim
		// tick stale relative to our snapshot. See the field doc on
		// MovingGridCorrection.PrevPoseCorrection.
		public static bool TryFindPrevPoseCorrection(Vector3D origin, out MatrixD correction)
		{
			for (var i = 0; i < _movingGridsCount; i++)
			{
				var distSq = Vector3D.DistanceSquared(origin, _movingGrids[i].VolumeCenter);
				if (distSq > _movingGrids[i].VolumeRadiusSq)
				{
					continue;
				}

				correction = _movingGrids[i].PrevPoseCorrection;
				return true;
			}

			correction = MatrixD.Identity;
			return false;
		}
	}

	// Per-render-frame state for one moving grid. Pre-flattened from the
	// SmoothedEntity record into the form the rebake hot path actually needs:
	// a sphere center + squared radius for the containment check, and the
	// rigid correction matrix the rebaker applies to billboard origins lying
	// inside the sphere.
	internal struct MovingGridCorrection
	{
		public Vector3D VolumeCenter;
		public double VolumeRadiusSq;
		public MatrixD Correction;
		// Same shape as Correction (`inv(reference) * smoothed`) but using the
		// grid's PREVIOUS sim-tick pose as the reference instead of the
		// current. The HUD-marker path needs this because POI WorldPositions
		// for ship-attached markers (broadcast antennas, etc.) are populated
		// from the antenna's world coord *one sim tick stale* relative to
		// our snapshot — so applying inv(grid_T) to them maps a
		// grid_T-1-frame point through grid_T's inverse, which produces an
		// off-by-one-tick marker (drifts opposite to ship motion by per-tick
		// magnitude). Line/point billboards don't have this staleness —
		// they're emitted post-physics at grid_T pose — so the rebake path
		// keeps using the current-pose correction.
		public MatrixD PrevPoseCorrection;
	}
}
