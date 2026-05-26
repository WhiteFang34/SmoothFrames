using VRageMath;

namespace SmoothFrames
{
	/// <summary>
	///     A render-object handle plus the entity's pose at the previous and
	///     current sim ticks. The render thread lerps between these on every
	///     render frame using the same alpha as the camera, so the entity
	///     moves in lockstep with the smoothed camera even when it isn't
	///     rigidly attached to it (e.g. a 3rd-person character the camera
	///     orbits, or a character whose head bone bobs within a sim tick).
	/// </summary>
	internal struct SmoothedEntity
	{
		// MyEntity.EntityId — lets render-thread consumers look up a
		// specific captured entity by id. PlacementPreviewSmoothing uses
		// this to find the host grid's smoothed pose when the placement
		// gizmo is snapped to a grid (the preview entity isn't in the
		// grid's RenderObjectIDs, so per-entity smoothing doesn't reach
		// it; we need the grid's pose to compute a delta and apply it to
		// the preview's separate render entities).
		public long EntityId;
		public uint[] RenderObjectIds;
		public Vector3D PreviousPosition;
		public Quaternion PreviousRotation;
		// Per-axis basis-vector lengths of the prev-tick WorldMatrix. Captured
		// because some smoothed entities (parachute canopy is the motivating
		// case — its `m_lastParachuteScale` is the billowing animation) carry
		// a non-identity scale in their world matrix, and reconstructing the
		// pose from rotation + translation only drops it. With scale dropped,
		// the first sign is `Quaternion.CreateFromRotationMatrix` reading
		// non-unit basis vectors and producing a degenerate quaternion that
		// rebuilds as a rank-deficient (visibly flat) matrix.
		public Vector3 PreviousScale;
		public Vector3D CurrentPosition;
		public Quaternion CurrentRotation;
		public Vector3 CurrentScale;

		// True when the entity is an IMyCubeGrid. Read on the render thread by
		// BillboardSmoothing to decide whether to register a moving-grid
		// correction for line/point billboards whose world origin lies on this
		// grid (Build Info block-edge highlights, terminal underlays, etc.).
		// Characters are excluded because a Build Info overlay never anchors
		// to a character body; mixing them in would risk applying a
		// character-pose correction to a HUD-attached line that just happens
		// to fall inside the character's bounding sphere.
		public bool IsGrid;

		// True when this entity is the local player's piloted ship grid (the
		// top-most parent of MySession.ControlledEntity, when piloting). The
		// render thread uses this to register the piloted ship's correction
		// matrix in a dedicated slot that HUD elements anchored to the ship
		// (the crosshair's "ship_pos + 1000 * forward" target, the artificial
		// horizon's gravity dot products) can look up directly — without going
		// through the sphere-containment test that the moving-grid registry
		// uses for billboards. The crosshair's anchor point sits 1000 m
		// forward of the ship, way outside any registered grid sphere.
		public bool IsPilotedShip;

		// World-space bounding sphere at the current sim tick — pulled from
		// MyEntity.PositionComp.WorldVolume. Used as the spatial test for
		// "does this billboard origin belong on this grid" on the render
		// thread. Only meaningful when IsGrid is true.
		public BoundingSphereD CurrentVolume;

		// Unparented MyParticleEffects reachable from this entity, captured on
		// the sim thread alongside the entity's vanilla pose. Discovered
		// generically by AttachedParticleCapture (field walk on the entity's
		// runtime type, recursing into composition objects and collections),
		// so the same correction lands on welder flame, tool sparks, drill
		// dust/spark, gun-loop muzzle flashes, ship-grinder sparks, and any
		// future or modded entity that holds an MyParticleEffect reference in
		// the same way — no allow-list to maintain.
		//
		// Filtered to state.ParentID == uint.MaxValue: the rest auto-follow
		// their parent actor on the renderer side (MyGPUEmitter.Update reads
		// MyActor.WorldMatrix of the parent and we've already overwritten
		// that with the smoothed pose), so the override path needs only the
		// unparented set, where the GPU emitter consumes baked sim-tick world
		// coords and the effect would otherwise be left at the unsmoothed
		// pose.
		//
		// Null/empty when no attached effects are active for this entity.
		// Capturing the (id, world matrix) pair on the sim thread (instead of
		// reading effect.WorldMatrix from the render thread) eliminates a
		// torn-MatrixD race at sim-tick boundaries: without this, a render
		// frame landing exactly between MyCharacterWeaponPositionComponent
		// updating the gun/tool's WorldMatrix and the per-tick
		// effect-position refresh (MyWelder.WorldPositionChanged for the
		// flame, MyEngineerToolBase.UpdateAfterSimulation for tool effects,
		// MyDrillBase.UpdateParticles for drill effects,
		// MyGunBase.UpdateEffectPositions for gun effects) could see
		// effect_(T+1) paired with our snapshot's entity_T pose and write the
		// effect to a position derived from the wrong delta — visible as an
		// occasional blip to a "second position".
		public AttachedParticle[] AttachedParticles;

		// Unparented MyLights tied to this entity, captured per sim tick.
		// Producers covered: character headlamp
		// (MyRenderComponentCharacter.m_light), welder/grinder tool
		// effect light (MyEngineerToolBase.m_toolEffectLight). Lights
		// parented in the renderer (block lights via per-cell cull objects,
		// jetpack thrust via the character cull) follow their parent
		// automatically and are not captured here. See AttachedLightCapture
		// for the rationale.
		//
		// Null/empty when no attached lights are active for this entity.
		public AttachedLightSnapshot[] AttachedLights;
	}

	/// <summary>
	///     Pose snapshot of one unparented <c>MyParticleEffect</c> attached to
	///     a smoothed entity. The render thread maps the captured vanilla
	///     world matrix onto the smoothed entity by post-multiplying by
	///     <c>inv(entity_T) * entity_smoothed</c> — see the comment on
	///     <see cref="SmoothedEntity.AttachedParticles"/> and
	///     <c>RenderFrameSmoothing.ApplyAttachedParticleSmoothing</c> for the
	///     derivation.
	/// </summary>
	internal struct AttachedParticle
	{
		public uint EffectId;
		public MatrixD VanillaMatrix;
	}

	/// <summary>
	///     A single frozen view of the camera at a particular sim tick.
	///     Position + rotation are kept separately so the render thread can
	///     quaternion-slerp between snapshots.
	/// </summary>
	internal struct CameraSnapshot
	{
		public long TimestampTicks;
		public Vector3D Position;
		public Quaternion Rotation;
		public float Fov;
		public float FovForSkybox;
		public Matrix ProjectionMatrix;
		public Matrix ProjectionMatrixFar;
		public float NearPlane;
		public float FarPlane;
		public float FarFarPlane;
		public bool Smooth;

		// Entities to keep rigidly attached to the smoothed camera each render
		// frame. Null when there's nothing to mirror this tick.
		public SmoothedEntity[] SmoothedEntities;
	}

	/// <summary>
	///     Thread-safe holder for the last two sim-tick camera snapshots.
	///     Written once per sim tick on the sim thread (via
	///     MyCamera.UploadViewMatrixToRender postfix), read many times per
	///     second on the render thread (via ProcessMessageQueue postfix).
	/// </summary>
	internal static class CameraHistory
	{
		private static readonly object Lock = new object();

		private static CameraSnapshot _previous;
		private static CameraSnapshot _current;
		private static int _validCount;

		public static void Push(ref CameraSnapshot snapshot)
		{
			lock (Lock)
			{
				// A camera cut invalidates any smooth relationship between the
				// previous snapshot and this one — clear history so we don't
				// interpolate across it.
				if (!snapshot.Smooth)
				{
					_current = snapshot;
					_validCount = 1;
					return;
				}

				_previous = _current;
				_current = snapshot;
				if (_validCount < 2)
				{
					_validCount++;
				}
			}
		}

		public static void Reset()
		{
			lock (Lock)
			{
				_validCount = 0;
			}
		}

		/// <summary>
		///     Try to read the two most recent snapshots. Returns false if
		///     fewer than two valid snapshots have been recorded or if the
		///     latest is a non-smooth update.
		/// </summary>
		public static bool TryGetPair(out CameraSnapshot previous, out CameraSnapshot current)
		{
			lock (Lock)
			{
				if (_validCount < 2 || !_current.Smooth)
				{
					previous = default;
					current = _validCount > 0 ? _current : default;
					return false;
				}

				previous = _previous;
				current = _current;
				return true;
			}
		}
	}
}
