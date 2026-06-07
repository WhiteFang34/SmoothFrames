using System;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SmoothFrames
{
	/// <summary>
	///     Build Info's overlay profile for the mod-agnostic recognizer
	///     (<see cref="DepthPulledOverlay"/>) — the one place SmoothFrames
	///     hard-codes Build Info's internals (its overlay material ids and the
	///     Short Range LIDAR preview-frustum constants). The engine never names
	///     this class; it goes through <see cref="DepthPulledOverlay"/>, which
	///     folds <see cref="OverlayMaterials"/> into its material set and routes
	///     the viewfinder test to <see cref="IsLidarPreviewSquareLine"/>. A second
	///     overlay mod would get its own <c>*Overlay</c> profile wired in the same
	///     way. See the <b>Mod-specific recognition</b> section of CLAUDE.md for
	///     why the depth-pull technique can't be recognized generically and the
	///     material is the discriminator.
	///
	///     What <c>/bi cn</c> draws: Build Info's conveyor-network overlay renders,
	///     once per sim tick, each conveyor port and connection depth-pulled close
	///     to the camera so it shows through the hull. Ports are
	///     <see cref="MyBillboard.LocalTypeEnum.Point"/> dots (caught geometrically
	///     in the engine, not by material); connections are
	///     <see cref="MyBillboard.LocalTypeEnum.Custom"/> lines, arrows, and boxes
	///     tagged with the <c>BuildInfo_Square</c> / <c>BuildInfo_ShadowedLine</c> /
	///     <c>BuildInfo_Arrow</c> / <c>BuildInfo_ShadowedArrow</c> materials below.
	///     They're matched by material because geometry can't separate a short
	///     conveyor segment — near-square after the depth-pull — from a HUD glyph.
	///
	///     The "Short Range LIDAR" (PhysicsSnapshot) MultiTool instrument draws two
	///     more pieces:
	///
	///     - A depth-pulled point cloud (<c>RenderPixels</c>): orient-less
	///       <see cref="MyBillboard.LocalTypeEnum.Custom"/> pixel quads bulk-emitted
	///       with <c>BuildInfo_Dot</c> (its <c>Mat_Dot2</c>). Build Info's other
	///       <c>Mat_Dot2</c> users emit <c>Point</c> billboards, which take the
	///       engine's geometric Point path and never reach the Custom-quad test, so
	///       this material safely tags only the cloud. Same depth-pull recovery as
	///       the conveyor quads.
	///     - A camera-anchored viewfinder square (<c>RenderSquare</c>): four
	///       <see cref="MyBillboard.LocalTypeEnum.Custom"/> laser lines at the
	///       preview frustum's near plane, redrawn each sim tick relative to the
	///       live camera. It rotates AND translates with the view, so it must take
	///       the full camera-relative transform, not the depth-pull translation.
	///       Material and thickness can't separate it from the Measure tool's
	///       depth-pulled see-through lines (both <c>BuildInfo_Laser</c> at 0.001
	///       thickness), so <see cref="IsLidarPreviewSquareLine"/> recognizes it by
	///       its exact near-plane corner geometry (the preview FOV / near-plane /
	///       aspect below): a depth-pulled line would have to land both endpoints on
	///       the preview frustum's near-plane corners, which it effectively never
	///       does. Hard-coding those constants means a Build Info change to the
	///       preview frustum degrades the viewfinder back to the sim-rate stutter
	///       rather than mis-correcting other content.
	///
	///     Material ids resolve through the global string-intern table, so they
	///     match the entry Build Info computes when it's loaded and a private one —
	///     which matches nothing — when it isn't; if Build Info renames them, the
	///     content degrades back to the generic path rather than crashing.
	/// </summary>
	internal static class BuildInfoOverlay
	{
		// Build Info's conveyor-overlay + LIDAR-cloud material ids (its
		// Constants.Mat_*). See the class doc for the load-order /
		// graceful-degradation rationale.
		private static readonly MyStringId _matSquare = MyStringId.GetOrCompute("BuildInfo_Square");
		private static readonly MyStringId _matLineShadow = MyStringId.GetOrCompute("BuildInfo_ShadowedLine");
		private static readonly MyStringId _matArrow = MyStringId.GetOrCompute("BuildInfo_Arrow");
		private static readonly MyStringId _matArrowShadow = MyStringId.GetOrCompute("BuildInfo_ShadowedArrow");

		// Build Info's Mat_Dot2 — the Short Range LIDAR point cloud
		// (PhysicsSnapshot.RenderPixels) draws each depth-pulled pixel with this.
		private static readonly MyStringId _matDot = MyStringId.GetOrCompute("BuildInfo_Dot");

		// Build Info's Mat_Laser — the Short Range LIDAR viewfinder square (also
		// the Measure tool's see-through lines, hence the geometric corner test in
		// IsLidarPreviewSquareLine rather than a material-only match).
		private static readonly MyStringId _matLaser = MyStringId.GetOrCompute("BuildInfo_Laser");

		/// <summary>
		///     Build Info's depth-pulled overlay-quad materials, folded into
		///     <see cref="DepthPulledOverlay"/>'s material set at load. Covers the
		///     conveyor lines/arrows/boxes (and their shadows) and the LIDAR point
		///     cloud — every bulk-emitted Custom quad Build Info depth-pulls. The
		///     dots are <c>Point</c> billboards handled geometrically, so they're
		///     not listed here.
		/// </summary>
		public static readonly MyStringId[] OverlayMaterials =
		{
			_matSquare,
			_matLineShadow,
			_matArrow,
			_matArrowShadow,
			_matDot,
		};

		// Build Info PhysicsSnapshot preview-frustum constants: its FOV (50°),
		// NearPlanePreview (0.1 m), and AspectRatio (1). Math.PI is a const, so
		// the FOV expression is a compile-time constant.
		private const double LidarPreviewFov = 50.0 * Math.PI / 180.0;
		private const double LidarPreviewNearPlane = 0.1;
		private const double LidarPreviewAspectRatio = 1.0;

		// Near-plane corner geometry derived from the constants above. A near
		// corner sits at forward depth == LidarPreviewNearPlane and lateral
		// offset == sqrt(halfWidth² + halfHeight²) from the camera. tan() isn't
		// const-foldable, so these are static readonly.
		private static readonly double _lidarPreviewNearHalfHeight =
			LidarPreviewNearPlane * Math.Tan(LidarPreviewFov * 0.5);
		private static readonly double _lidarPreviewNearHalfWidth =
			_lidarPreviewNearHalfHeight * LidarPreviewAspectRatio;
		private static readonly double _lidarPreviewCornerLateral = Math.Sqrt(
			_lidarPreviewNearHalfWidth * _lidarPreviewNearHalfWidth
			+ _lidarPreviewNearHalfHeight * _lidarPreviewNearHalfHeight);

		// Match tolerances (m). The viewfinder corners are exact, so these only
		// need to absorb the float round-trip through emission; they're tight
		// enough that a Measure see-through line would have to place both
		// endpoints within ~2 cm of the preview frustum's near-plane corners to
		// be mistaken for it.
		private const double LidarPreviewDepthTolerance = 0.02;
		private const double LidarPreviewLateralTolerance = 0.02;

		/// <summary>
		///     True when a line billboard is an edge of the Short Range LIDAR
		///     viewfinder square — a <c>Mat_Laser</c> line whose two endpoints both
		///     sit on Build Info's preview-frustum near plane (forward depth ≈
		///     <see cref="LidarPreviewNearPlane"/>, lateral offset ≈ the near-plane
		///     corner radius) in the camera frame that emitted it.
		///     <see cref="DepthPulledOverlay.IsCameraAnchoredViewfinderLine"/> calls
		///     this for the engine; see the class doc for why geometry, not
		///     material, is the discriminator.
		/// </summary>
		public static bool IsLidarPreviewSquareLine(Vector3D endpointA, Vector3D endpointB,
			MyStringId material, Vector3D vanillaCameraPos, Quaternion vanillaCameraRotation)
		{
			if (material != _matLaser)
			{
				return false;
			}

			// Forward axis of the emitting camera. VanillaCameraRotation was built
			// from the camera world matrix, so this round-trips to its Forward;
			// only the forward direction and offset magnitudes are used, so
			// Right/Up axis identity doesn't matter.
			var camForward = (Vector3D)Matrix.CreateFromQuaternion(vanillaCameraRotation).Forward;

			return IsLidarPreviewNearCorner(endpointA, vanillaCameraPos, camForward)
				&& IsLidarPreviewNearCorner(endpointB, vanillaCameraPos, camForward);
		}

		private static bool IsLidarPreviewNearCorner(Vector3D worldPoint, Vector3D cameraPos,
			Vector3D cameraForward)
		{
			var offset = worldPoint - cameraPos;
			var depth = Vector3D.Dot(offset, cameraForward);
			if (Math.Abs(depth - LidarPreviewNearPlane) > LidarPreviewDepthTolerance)
			{
				return false;
			}

			var lateral = Math.Sqrt(Math.Max(0.0, offset.LengthSquared() - depth * depth));
			return Math.Abs(lateral - _lidarPreviewCornerLateral) <= LidarPreviewLateralTolerance;
		}
	}
}
