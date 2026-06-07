using System.Collections.Generic;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SmoothFrames
{
	/// <summary>
	///     Mod-agnostic recognizer for the see-through-walls overlay content the
	///     billboard smoother (<see cref="BillboardSmoothing"/>) has to
	///     special-case. The engine talks only to this class; the hard-coded mod
	///     knowledge lives in a per-mod profile (<see cref="BuildInfoOverlay"/>).
	///
	///     The technique these overlays share: bake a world target's position as
	///     <c>posClose = cam + (target − cam) × NearRatio</c> for a small ratio so
	///     the content draws in front of the hull, then draw it with
	///     <c>BlendTypeEnum.PostPP</c> (the engine's after-post-processing pass).
	///     That technique can't be recognized generically from a billboard alone,
	///     which is why this class still needs material identity for the
	///     bulk-emitted Custom quads:
	///
	///     - <b>PostPP is shared.</b> The HUD frameworks (Rich HUD Master, Text
	///       HUD API) draw essentially all their content with PostPP too, so the
	///       blend can't separate a world overlay from screen HUD.
	///     - <b>Position can't either.</b> A depth-pulled overlay sits so close to
	///       the camera that across a sim tick it translates by ≈ the camera delta
	///       — the same as camera-locked HUD; the two diverge only under rotation,
	///       where per-element tracking is unreliable. Geometry fails as well: a
	///       short conveyor segment is near-square after the pull, indistinguish-
	///       able from a HUD glyph (see "Considered and rejected" in CLAUDE.md).
	///
	///     The overlay <i>materials</i> are therefore the discriminator. Build
	///     Info's are built in (<see cref="BuildInfoOverlay.OverlayMaterials"/>); a
	///     second overlay mod adds its materials to a profile the same way.
	///     Material ids resolve through the global string-intern table, so an
	///     absent mod's name simply matches nothing — graceful degradation, never
	///     a crash.
	///
	///     Only the bulk-emitted Custom quads and the camera-anchored viewfinder
	///     reach this class. The depth-pulled dots
	///     (<c>BillboardSmoothing.TryRebakePointDepthPulled</c>) and the
	///     orient-carrying thin lines/points (<c>RebakeLine</c>,
	///     <c>RebakeAxisAligned</c>) are caught geometrically and need no material
	///     — near-camera <c>Point</c>s and hairline lines are rare, so the
	///     geometric signal is clean there. Custom near-camera quads are dominated
	///     by HUD, so the same trick can't work for them.
	/// </summary>
	internal static class DepthPulledOverlay
	{
		/// <summary>
		///     Depth-pull geometry constant. See-through overlays bake position as
		///     <c>posClose = cam + (target − cam) × NearRatio</c>; <c>0.01</c> is
		///     the near-universal value for the technique (Build Info's
		///     <c>DepthRatio</c>, its LIDAR tool's <c>PixelDepthRatio</c>, …). The
		///     common recovery (<see cref="Complement"/> translation) is
		///     insensitive to the exact value — the residual is a sub-mm fraction
		///     of the per-tick camera motion for any ratio in 0.001–0.05 — so this
		///     is a general default, not a constant mirrored from one mod. Only the
		///     moving-grid un-pull path (<see cref="InverseNearRatio"/>, recovering
		///     a depth-pulled line's true target on a coasting ship) is
		///     ratio-sensitive, and 0.01 covers the known cases. The ratio is shared
		///     across profiles, not per-mod; a second overlay mod that pulls at a
		///     different ratio would need this made per-profile.
		/// </summary>
		public const double NearRatio = 0.01;
		public const double InverseNearRatio = 1.0 / NearRatio;
		public const double Complement = 1.0 - NearRatio;

		// Overlay materials, aggregated from the built-in mod profiles (currently
		// BuildInfoOverlay). Built once at type-init and never mutated, so the
		// render thread reads it freely. Code-based for now — a new overlay mod
		// adds its materials in BuildMaterialSet; a runtime-configurable list was
		// deferred until more such mods surface.
		private static readonly HashSet<MyStringId> _overlayMaterials = BuildMaterialSet();

		private static HashSet<MyStringId> BuildMaterialSet()
		{
			// MyStringId.Comparer: the struct has no IEquatable<MyStringId>, so the
			// default comparer boxes on every lookup — the engine's own MyStringId
			// doc warns to always pass Comparer for hash containers, and Contains()
			// runs per Custom billboard per render frame.
			var set = new HashSet<MyStringId>(MyStringId.Comparer);

			// Built-in profiles. A second overlay mod would add its material array
			// here (and, if it has one, a viewfinder test in
			// IsCameraAnchoredViewfinderLine).
			foreach (var material in BuildInfoOverlay.OverlayMaterials)
			{
				set.Add(material);
			}

			return set;
		}

		/// <summary>
		///     True when the billboard is a depth-pulled see-through overlay quad —
		///     a <see cref="MyBillboard.LocalTypeEnum.Custom"/> line, arrow, box, or
		///     point-cloud pixel whose material is a registered overlay material and
		///     whose every corner sits within <paramref name="nearThresholdSq"/>
		///     (m²) of the vanilla camera. The near-camera gate is a sanity check
		///     that keeps the caller's translation recovery off any non-depth-pulled
		///     use of the same material. Triangle and non-Custom billboards are
		///     excluded — a quad is Custom, and triangles leave Position3 unset.
		/// </summary>
		public static bool IsDepthPulledOverlayQuad(MyBillboard billboard, Vector3D vanillaCameraPos,
			double nearThresholdSq)
		{
			// A depth-pulled overlay quad is LocalType.Custom. Guarding here — rather
			// than relying on the caller's dispatch handling Points first — keeps a
			// depth-pulled Point that happens to share an overlay material (e.g. Build
			// Info's Mat_Dot2 conveyor ports) from being misread as a quad and having
			// its radius/angle slots overwritten if the dispatch loop is ever reordered.
			if (billboard.LocalType != MyBillboard.LocalTypeEnum.Custom)
			{
				return false;
			}

			if (billboard is MyTriangleBillboard)
			{
				return false;
			}

			// Material check first (cheap, and the usual reject), then the
			// near-camera sanity gate on every corner.
			var material = billboard.Material;
			return _overlayMaterials.Contains(material)
				&& Vector3D.DistanceSquared(billboard.Position0, vanillaCameraPos) <= nearThresholdSq
				&& Vector3D.DistanceSquared(billboard.Position1, vanillaCameraPos) <= nearThresholdSq
				&& Vector3D.DistanceSquared(billboard.Position2, vanillaCameraPos) <= nearThresholdSq
				&& Vector3D.DistanceSquared(billboard.Position3, vanillaCameraPos) <= nearThresholdSq;
		}

		/// <summary>
		///     True when a line billboard is an edge of an overlay mod's
		///     camera-anchored viewfinder square — content that rotates AND
		///     translates with the view and so must take the full camera-relative
		///     transform rather than the depth-pull translation. This is the
		///     camera-anchored sibling of the depth-pulled overlays: it shares their
		///     material, thickness, and PostPP blend, so it can only be told apart
		///     by its exact near-plane corner geometry, which is necessarily
		///     mod-specific. Delegates to the one known profile — Build Info's Short
		///     Range LIDAR viewfinder; a second mod with a viewfinder adds its test
		///     here.
		/// </summary>
		public static bool IsCameraAnchoredViewfinderLine(Vector3D endpointA, Vector3D endpointB,
			MyStringId material, Vector3D vanillaCameraPos, Quaternion vanillaCameraRotation)
		{
			return BuildInfoOverlay.IsLidarPreviewSquareLine(endpointA, endpointB, material,
				vanillaCameraPos, vanillaCameraRotation);
		}
	}
}
