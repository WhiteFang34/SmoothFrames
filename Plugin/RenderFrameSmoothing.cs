using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VRage.Library.Utils;
using VRage.Render.Scene;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace SmoothFrames
{
	/// <summary>
	///     Harmony patch on <c>MyRender11.ProcessMessageQueue</c>. The prefix
	///     snaps the vanilla camera pose at frame start (paired atomically
	///     with the engine's BillboardsRead inside ProcessMessageQueue), and
	///     the postfix runs the per-frame smoothing pipeline. Both delegate
	///     to <see cref="RenderFrameSmoothing"/> for the actual work.
	///     <c>MyRender11</c> is internal in <c>VRage.Render11</c>, so we
	///     resolve the target by name.
	/// </summary>
	[HarmonyPatch]
	public static class PatchProcessMessageQueue
	{
		public static MethodBase TargetMethod()
		{
			return AccessTools.Method("VRageRender.MyRender11:ProcessMessageQueue", new[] { typeof(bool) })
				?? throw Errors.NotResolved("VRageRender.MyRender11.ProcessMessageQueue");
		}

		public static void Prefix(bool draw)
		{
			if (!draw)
			{
				return;
			}

			RenderFrameSmoothing.SnapVanillaPose();
		}

		public static void Postfix(bool draw)
		{
			if (!draw)
			{
				return;
			}

			RenderFrameSmoothing.RunFrame();
		}
	}

	/// <summary>
	///     Render-thread smoothing pipeline. Holds the per-frame smoothed
	///     camera pose and the vanilla pose snapped at frame start, runs the
	///     lerp/slerp + entity overrides + particle / placement-preview /
	///     HUD-marker / horizon refresh once per render frame.
	///
	///     Driven by <see cref="PatchProcessMessageQueue"/>. Public state
	///     (smoothed/vanilla poses, validity flag,
	///     <see cref="TryGetSmoothedPose"/>) is read by the other smoothing
	///     modules — they take their per-frame deltas against this class's
	///     published values.
	/// </summary>
	public static class RenderFrameSmoothing
	{
		// Reuse one synthetic message — the render thread is the only writer,
		// SetupCameraMatrices reads fields synchronously, so no GC per frame.
		private static readonly MyRenderMessageSetCameraViewMatrix _synthMessage =
			new MyRenderMessageSetCameraViewMatrix();

		private static readonly Action<MyRenderMessageSetCameraViewMatrix> _setupCameraMatrices =
			ResolveSetupCameraMatrices();

		// Shared by both the sync path and the particle path below.
		private static readonly Type _managersType = AccessTools.TypeByName("VRage.Render11.Common.MyManagers")
			?? throw Errors.NotResolved("VRage.Render11.Common.MyManagers");

		// MyManagers.PostponedUpdate.{SavePostponedUpdate, ApplyPostponedUpdate}
		// are internal; the field/method lookups resolve at startup, but
		// binding the delegates needs the instance, which the engine
		// constructs later. Defer binding to TryResolveSyncPath.
		private static readonly FieldInfo _postponedUpdateField = AccessTools.Field(_managersType, "PostponedUpdate")
			?? throw Errors.NotResolved("MyManagers.PostponedUpdate");
		private static readonly MethodInfo _savePostponedUpdateMethod =
			AccessTools.Method(_postponedUpdateField.FieldType, "SavePostponedUpdate")
			?? throw Errors.NotResolved("PostponedUpdate.SavePostponedUpdate");
		private static readonly MethodInfo _applyPostponedUpdateMethod =
			AccessTools.Method(_postponedUpdateField.FieldType, "ApplyPostponedUpdate")
			?? throw Errors.NotResolved("PostponedUpdate.ApplyPostponedUpdate");
		private static Action<MyRenderMessageUpdateRenderObject> _savePostponedUpdate;
		private static Action<uint> _applyPostponedUpdate;

		// Reused message for synchronous PostponedUpdate calls.
		// SavePostponedUpdate swaps Data with a pool entry, so we re-set Data
		// fields on every call.
		private static readonly MyRenderMessageUpdateRenderObject _entityUpdateMessage =
			new MyRenderMessageUpdateRenderObject();

		// MyManagers.ParticleEffectsManager.Get(uint) returns the render-side
		// MyRenderParticleEffect for a given sim-side MyParticleEffect.Id.
		// MyRenderParticleEffect is `internal`, m_state is `private`, so we
		// reach both via reflection — UpdateState bound through a hand-rolled
		// DynamicMethod thunk that handles the object→internal receiver
		// downcast we can't express directly in source. Harmony's
		// AccessTools.MethodDelegate falls back to plain
		// Delegate.CreateDelegate for open-instance binds, which on .NET 10
		// rejects `object` as the first delegate parameter when the
		// declaring type is internal ("signature is not compatible") — so we
		// emit the cast ourselves. Per-frame hot path then dispatches via
		// the cached delegate rather than MethodInfo.Invoke (which
		// box-allocates every argument and walks the reflection cache per
		// call).
		//
		// Per-render-frame override path:
		//   var fx = _particleManagerGet(id);
		//   var state = (MyParticleEffectState) _particleStateField.GetValue(fx);
		//   state.WorldMatrix = smoothed;
		//   state.TransformDirty = true;
		//   _particleUpdateState(fx, ref state);
		//
		// UpdateState iterates m_generations and flips SetTransformDirty on
		// each MyParticleGPUGeneration; the next per-frame Draw() then writes
		// EmitterData.WorldPosition from the new matrix, which the GPU
		// emitter consumes when MyGPUEmitters.Gather runs after our postfix.
		// Survives MyRenderParticleEffect.Update() between our hook and the
		// draw because the affected effects (welder flame, tool sparks, drill
		// dust/spark, gun-loop flashes) don't carry a velocity — Update only
		// integrates Translation when m_state.Velocity.HasValue.
		private delegate void UpdateStateDelegate(object instance, ref MyParticleEffectState state);

		private static readonly FieldInfo _particleManagerField =
			AccessTools.Field(_managersType, "ParticleEffectsManager")
			?? throw Errors.NotResolved("MyManagers.ParticleEffectsManager");
		private static readonly Type _renderParticleEffectType =
			AccessTools.TypeByName("VRage.Render11.Particles.MyRenderParticleEffect")
			?? throw Errors.NotResolved("MyRenderParticleEffect");
		private static readonly FieldInfo _particleStateField = AccessTools.Field(_renderParticleEffectType, "m_state")
			?? throw Errors.NotResolved("MyRenderParticleEffect.m_state");
		private static readonly UpdateStateDelegate _particleUpdateState = BuildParticleUpdateStateThunk();

		private static UpdateStateDelegate BuildParticleUpdateStateThunk()
		{
			var method = AccessTools.Method(_renderParticleEffectType, "UpdateState",
				new[] { typeof(MyParticleEffectState).MakeByRefType() })
				?? throw Errors.NotResolved("MyRenderParticleEffect.UpdateState");

			var dm = new DynamicMethod(
				"SmoothFrames_MyRenderParticleEffect_UpdateState_Thunk",
				typeof(void),
				new[] { typeof(object), typeof(MyParticleEffectState).MakeByRefType() },
				_renderParticleEffectType,
				skipVisibility: true);
			var il = dm.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Castclass, _renderParticleEffectType);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Callvirt, method);
			il.Emit(OpCodes.Ret);
			return (UpdateStateDelegate)dm.CreateDelegate(typeof(UpdateStateDelegate));
		}
		// Bound after the render-side manager is constructed — see
		// TryResolveParticleManager — because Get(uint) is resolved on the
		// runtime instance type (the field's declared type may be abstract).
		private static Func<uint, object> _particleManagerGet;
		private static bool _particleManagerGetMissing;

		// Last smoothed camera pose. Written here once per render frame after
		// the camera lerp/slerp, read by BillboardSmoothing when re-baking
		// Custom billboards before they're gathered into vertex buffers.
		// Same render thread, no synchronization needed.
		public static Vector3D LastSmoothedCameraPosition { get; private set; }
		public static Quaternion LastSmoothedCameraRotation { get; private set; }

		// `(now - curr.TimestampTicks) / dt` — same alpha that drove the
		// camera lerp/slerp this render frame. Read by BillboardSmoothing's
		// ordinal-based fallback so HUD-framework billboards (RHM, Text HUD
		// API) interpolate between their previous-tick and current-tick
		// emitted positions on the same curve as the camera. Stays at 0
		// before the first valid render frame computes one.
		public static double LastSmoothingAlpha { get; private set; }

		// Vanilla (sim-tick) camera pose snapped at the very start of the
		// current render frame, before the engine's RefreshRead picks up the
		// latest billboard CommitWrite inside ProcessMessageQueue. The snap
		// is paired with BillboardsRead to within nanoseconds — sim thread
		// has at most a few CPU cycles to slip in a new CameraHistory.Push
		// between our Prefix and the engine's RefreshRead — so the
		// camera-anchored billboard rebake's vanilla pose stays aligned with
		// the billboards it's transforming. Re-reading CameraHistory.TryGetPair
		// later in the render frame (e.g., in our Postfix or in
		// BillboardSmoothing) opens a millisecond-scale window during which
		// sim can push a new tick, making vanilla one tick newer than
		// BillboardsRead — the cause of the BV3 menu's occasional
		// one-tick-of-camera-motion flicker.
		internal static long FrameVanillaTickTimestamp { get; private set; }
		internal static Vector3D FrameVanillaCameraPosition { get; private set; }
		internal static Quaternion FrameVanillaCameraRotation { get; private set; }
		internal static bool FrameVanillaValid { get; private set; }

		// Runs before the engine's RefreshRead (the very first thing inside
		// ProcessMessageQueue is GetRenderFrame, which calls RefreshRead).
		// Snapping CameraHistory's curr here means the snap and
		// BillboardsRead are atomic to within the few CPU cycles between
		// Prefix exit and the engine's RefreshRead — the only consistent way
		// to pair (vanilla-camera-pose-at-this-frame-start,
		// BillboardsRead-at-this-frame-start) without hooking internal
		// render-side machinery.
		internal static void SnapVanillaPose()
		{
			if (CameraHistory.TryGetPair(out _, out var curr))
			{
				FrameVanillaTickTimestamp = curr.TimestampTicks;
				FrameVanillaCameraPosition = curr.Position;
				FrameVanillaCameraRotation = curr.Rotation;
				FrameVanillaValid = true;
			}
			else
			{
				FrameVanillaValid = false;
			}
		}

		/// <summary>
		///     Compute the smoothed camera pose for "now" using the same
		///     1-tick-delayed snapshot interpolation as the per-render-frame
		///     postfix. Used by main-thread patches (e.g. block-placement
		///     gizmo) that need a fresh smoothed pose between render-thread
		///     updates of <see cref="LastSmoothedCameraPosition"/>; reading
		///     that field directly would lag by up to one render frame.
		/// </summary>
		public static bool TryGetSmoothedPose(out Vector3D position, out Quaternion rotation)
		{
			position = default;
			rotation = Quaternion.Identity;

			if (!Plugin.InterpolationEnabled)
			{
				return false;
			}

			if (!CameraHistory.TryGetPair(out var prev, out var curr))
			{
				return false;
			}

			var now = Stopwatch.GetTimestamp();
			var dt = curr.TimestampTicks - prev.TimestampTicks;
			if (dt <= 0)
			{
				return false;
			}

			var alpha = (double)(now - curr.TimestampTicks) / dt;
			if (alpha < 0.0)
			{
				alpha = 0.0;
			}
			if (alpha > 1.0)
			{
				alpha = 1.0;
			}

			Vector3D.Lerp(ref prev.Position, ref curr.Position, alpha, out position);
			rotation = Quaternion.Slerp(prev.Rotation, curr.Rotation, (float)alpha);
			return true;
		}

		private static bool TryResolveParticleManager()
		{
			if (_particleManagerGet != null)
			{
				return true;
			}
			if (_particleManagerGetMissing)
			{
				return false;
			}

			var manager = _particleManagerField.GetValue(null);
			if (manager == null)
			{
				// Render manager not constructed yet — retry next frame.
				return false;
			}

			// Get(uint) is on the manager's runtime type, not the field's
			// declared type, which may be abstract. If it's missing here,
			// SE's particle manager API has changed — log once and stop
			// retrying (waiting won't help).
			var getMethod = AccessTools.Method(manager.GetType(),
				"Get", new[] { typeof(uint) });
			if (getMethod == null)
			{
				MyLog.Default.WriteLineAndConsole(
					"SmoothFrames: " + manager.GetType().FullName +
					".Get(uint) not resolved");
				_particleManagerGetMissing = true;
				return false;
			}

			// Closed delegate baked against the manager instance. Harmony's
			// thunk handles the MyRenderParticleEffect→object return
			// upcast that Delegate.CreateDelegate's relaxed binding would
			// also permit, but going through MethodDelegate keeps both
			// particle delegates on the same emission path.
			_particleManagerGet = AccessTools.MethodDelegate<Func<uint, object>>(getMethod, manager);
			return true;
		}

		private static bool TryResolveSyncPath()
		{
			if (_savePostponedUpdate != null)
			{
				return true;
			}

			var instance = _postponedUpdateField.GetValue(null);
			if (instance == null)
			{
				// Render manager isn't constructed yet — retry next frame.
				return false;
			}

			_savePostponedUpdate = (Action<MyRenderMessageUpdateRenderObject>)Delegate.CreateDelegate(
				typeof(Action<MyRenderMessageUpdateRenderObject>), instance, _savePostponedUpdateMethod);
			_applyPostponedUpdate = (Action<uint>)Delegate.CreateDelegate(
				typeof(Action<uint>), instance, _applyPostponedUpdateMethod);
			return true;
		}

		private static Action<MyRenderMessageSetCameraViewMatrix> ResolveSetupCameraMatrices()
		{
			var method = AccessTools.Method("VRageRender.MyRender11:SetupCameraMatrices",
					new[] { typeof(MyRenderMessageSetCameraViewMatrix) })
				?? throw Errors.NotResolved("VRageRender.MyRender11.SetupCameraMatrices");
			return (Action<MyRenderMessageSetCameraViewMatrix>)Delegate.CreateDelegate(
				typeof(Action<MyRenderMessageSetCameraViewMatrix>), method);
		}

		internal static void RunFrame()
		{
			// Clear last frame's moving-grid corrections unconditionally so
			// every code path below (interp disabled, no snapshot pair, no
			// captured entities) leaves a clean slate for the upcoming
			// MyBillboardRenderer.Gather. Otherwise a toggle from interp-on
			// to interp-off would leave stale corrections wired to
			// RebakeLine and the next on-foot long-span rebake would apply a
			// frozen vanilla-pose grid mapping that no longer matches the
			// engine.
			BillboardCorrections.BeginFrame();

			if (!Plugin.InterpolationEnabled)
			{
				return;
			}

			if (!CameraHistory.TryGetPair(out var prev, out var curr))
			{
				// Fewer than two snapshots, or last snapshot was a cut. Let
				// vanilla camera state stand for this frame.
				return;
			}

			var now = Stopwatch.GetTimestamp();
			var dt = curr.TimestampTicks - prev.TimestampTicks;
			if (dt <= 0)
			{
				return;
			}

			// Snapshot-interpolation with 1-tick delay: render the camera as it
			// was at (now - dt), which lies on the prev→curr segment.
			//   alpha = (t_target - prev) / dt = (now - dt - prev) / dt = (now - curr) / dt
			var alpha = (double)(now - curr.TimestampTicks) / dt;
			if (alpha < 0.0)
			{
				alpha = 0.0;
			}

			if (alpha > 1.0)
			{
				alpha = 1.0;
			}

			Vector3D.Lerp(ref prev.Position, ref curr.Position, alpha, out var interpPos);

			var interpRot = Quaternion.Slerp(prev.Rotation, curr.Rotation, (float)alpha);

			var rotMat = Matrix.CreateFromQuaternion(interpRot);
			MatrixD world = rotMat;
			world.Translation = interpPos;
			MatrixD.Invert(ref world, out var view);

			LastSmoothedCameraPosition = interpPos;
			LastSmoothedCameraRotation = interpRot;
			LastSmoothingAlpha = alpha;

			_synthMessage.ViewMatrix = view;
			_synthMessage.CameraPosition = interpPos;
			_synthMessage.FOV = curr.Fov;
			_synthMessage.FOVForSkybox = curr.FovForSkybox;
			_synthMessage.ProjectionMatrix = curr.ProjectionMatrix;
			_synthMessage.ProjectionFarMatrix = curr.ProjectionMatrixFar;
			_synthMessage.NearPlane = curr.NearPlane;
			_synthMessage.FarPlane = curr.FarPlane;
			_synthMessage.FarFarPlane = curr.FarFarPlane;
			_synthMessage.ProjectionOffsetX = 0f;
			_synthMessage.ProjectionOffsetY = 0f;
			_synthMessage.LastMomentUpdateIndex = 1;
			_synthMessage.Smooth = true;
			_synthMessage.UpdateTime = new MyTimeSpan(now);

			_setupCameraMatrices(_synthMessage);

			// Both the entity-smoothing path and the placement-preview override
			// below need the synchronous PostponedUpdate bypass. Resolve
			// once; if the render manager isn't constructed yet we'll retry
			// next frame.
			if (!TryResolveSyncPath())
			{
				return;
			}

			// Interpolate each captured entity's pose between prev and curr
			// snapshots using the same alpha as the camera, then push the
			// result synchronously through PostponedUpdate (the queued
			// MyRenderProxy.UpdateRenderObject path lands one frame late and
			// loses ordering to the sim thread's next per-tick message).
			//
			// Side effect: for each grid in the captured list, register a
			// (vanilla-pose bounding sphere + rigid correction matrix) record
			// with BillboardSmoothing so its later Gather-time rebake can
			// re-anchor any line/point billboards whose origin lies on this
			// grid (Build Info block-edge highlights on a moving ship while
			// the player is on jetpack — the camera doesn't follow the ship
			// there so the existing piloting branch can't catch this case).
			// The per-frame clear lives at the top of RunFrame so it runs
			// even on the interp-disabled / no-snapshot-pair paths.
			var entities = curr.SmoothedEntities;
			if (entities != null && entities.Length > 0)
			{
				var alphaF = (float)alpha;
				for (var i = 0; i < entities.Length; i++)
				{
					Vector3D.Lerp(ref entities[i].PreviousPosition, ref entities[i].CurrentPosition,
						alpha, out var smoothPos);

					var smoothRot = Quaternion.Slerp(entities[i].PreviousRotation, entities[i].CurrentRotation, alphaF);

					// Lerp scale alongside rotation/translation so entities whose
					// world matrix carries non-identity scale (e.g. the
					// parachute canopy's billowing animation) recompose
					// correctly. Reconstructing from rotation + translation
					// only would produce a rank-deficient matrix and render
					// the canopy flat.
					Vector3.Lerp(ref entities[i].PreviousScale, ref entities[i].CurrentScale,
						alphaF, out var smoothScale);

					MatrixD smoothMatrix = Matrix.CreateScale(smoothScale) * Matrix.CreateFromQuaternion(smoothRot);
					smoothMatrix.Translation = smoothPos;

					if (entities[i].IsGrid && entities[i].CurrentVolume.Radius > 0)
					{
						RegisterGridForBillboardRebake(ref entities[i], ref smoothMatrix);
					}

					var ids = entities[i].RenderObjectIds;
					foreach (var id in ids)
					{
						if (id == uint.MaxValue)
						{
							continue;
						}

						_entityUpdateMessage.ID = id;
						_entityUpdateMessage.Data.WorldMatrix = smoothMatrix;
						_entityUpdateMessage.Data.HasWorldMatrix = true;

						_savePostponedUpdate(_entityUpdateMessage);
						_applyPostponedUpdate(id);
					}

					ApplyAttachedParticleSmoothing(ref entities[i], ref smoothMatrix);

					ApplyAttachedLightSmoothing(entities[i].AttachedLights, alpha);
				}
			}

			ApplyPlacementPreviewSmoothing(ref world);

			// HUD GPS marker icons / labels: refresh tagged sprite messages
			// to the smoothed-camera-projected screen position THIS render
			// frame, before RenderMainSprites consumes the bucket. The
			// previous prefix-on-MySpritesRenderer.ProcessDrawMessage path
			// fired only once per sim tick (sprite messages drained on first
			// render frame after sim emission, then no work for the other
			// render frames), producing a fixed offset per inter-tick
			// indistinguishable from vanilla. This per-render-frame loop
			// updates each tagged message's Position field freshly each
			// render frame so the icon visibly tracks the smoothed scene.
			HudMarkerSmoothing.RefreshAllTrackedSpritesPerRenderFrame();

			// Artificial-horizon level line + altitude. Same shape as the
			// HUD-marker refresh: re-derive position (anchored on the
			// smoothed crosshair projection) and rotation (atan2 of the
			// piloted ship's smoothed Right/Up dotted with gravity) against
			// this render frame's smoothed pose. Lands BEFORE
			// RenderMainSprites consumes the queued sprite-ext message.
			ArtificialHorizonSmoothing.MutateLevelLine();
		}

		// Build the rigid correction matrix that maps a world point at the
		// grid's vanilla (sim-tick) pose to the corresponding world point at
		// its smoothed pose: `correction = inv(curr_world) * smoothed_world`.
		// Applying this to a billboard's emitted world coordinates puts them
		// on the smoothed grid mesh, which is where the renderer is drawing
		// the actual block. Registered per render frame; consumed by
		// BillboardSmoothing.RebakeLine / RebakeAxisAligned via the
		// BillboardCorrections.TryFindCorrection lookup.
		private static void RegisterGridForBillboardRebake(ref SmoothedEntity entity, ref MatrixD smoothMatrix)
		{
			var currRotMat = Matrix.CreateFromQuaternion(entity.CurrentRotation);
			MatrixD currMatrix = currRotMat;
			currMatrix.Translation = entity.CurrentPosition;

			MatrixD.Invert(ref currMatrix, out var currInv);

			var correction = currInv * smoothMatrix;

			// Same shape but using the previous sim-tick pose for the inverse
			// — needed by HUD markers whose POI WorldPosition is populated
			// from the antenna's world coord one sim tick stale. See the
			// field doc on MovingGridCorrection in BillboardCorrections.
			var prevRotMat = Matrix.CreateFromQuaternion(entity.PreviousRotation);
			MatrixD prevMatrix = prevRotMat;
			prevMatrix.Translation = entity.PreviousPosition;

			MatrixD.Invert(ref prevMatrix, out var prevInv);

			var prevPoseCorrection = prevInv * smoothMatrix;

			BillboardCorrections.Register(entity.CurrentVolume, ref correction, ref prevPoseCorrection);

			// Piloted ship: stash the current-pose correction in the
			// dedicated direct-lookup slot too. See
			// SmoothedEntity.IsPilotedShip's doc-comment for why this is
			// needed alongside the sphere-keyed registry above.
			if (entity.IsPilotedShip)
			{
				BillboardCorrections.SetPilotedShipCorrection(ref correction);
			}
		}

		// Per-render-frame override for particle effects attached to a smoothed
		// entity but unparented on the render side. AttachedParticleCapture
		// filters to the state.ParentID == uint.MaxValue case so this loop
		// only sees effects whose GPU emitter consumes the baked sim-tick
		// world coords directly — the parented set already auto-follows the
		// smoothed parent actor on the renderer side and would be corrupted
		// by overwriting state.WorldMatrix (which the renderer treats as a
		// local position when ParentID is set).
		//
		// Math (XNA row-vector convention: `child.world = local * parent.world`).
		// Each effect's world matrix at sim tick T was authored as
		// `effect_T = effect_local * host_T`, where `host_T` is the gun/tool
		// pose used to compute `effect_T` on the sim thread (welder's
		// WorldPositionChanged writes flame.WorldMatrix from GetEffectMatrix;
		// MyEngineerToolBase.UpdateAfterSimulation writes
		// m_toolEffect.WorldMatrix; MyDrillBase.UpdateParticles writes the
		// dust/spark world matrices; MyGunBase.UpdateEffectPositions writes
		// loop-effect matrices). We want the smoothed effect to track the
		// smoothed host: `effect_smooth = effect_local * host_smooth`. Apply
		// the host's vanilla→smoothed transform
		// `delta = inv(host_T) * host_smooth` as a post-multiply on the
		// effect's vanilla matrix:
		//
		//     effect_smooth = effect_T * delta
		//                   = effect_local * host_T * inv(host_T) * host_smooth
		//                   = effect_local * host_smooth.   ✓
		//
		// Avoids ever materializing effect_local explicitly — only needs the
		// host's two poses (already in SmoothedEntity) and each effect's
		// vanilla matrix (captured into AttachedParticles[i].VanillaMatrix on
		// the sim thread, paired with host_T so a sim-tick boundary race
		// can't pair host_T with effect_(T+1)).
		private static void ApplyAttachedParticleSmoothing(ref SmoothedEntity entity, ref MatrixD smoothedHost)
		{
			var attached = entity.AttachedParticles;
			if (attached == null || attached.Length == 0 || !TryResolveParticleManager())
			{
				return;
			}

			// `delta` is independent of the effect — compute once, apply per
			// effect. host_T is reconstructed from CurrentPosition +
			// CurrentRotation (scale not needed; the host's basis is
			// orthonormal at the resolution we care about).
			var hostRotMat = Matrix.CreateFromQuaternion(entity.CurrentRotation);
			MatrixD hostVanilla = hostRotMat;
			hostVanilla.Translation = entity.CurrentPosition;

			MatrixD.Invert(ref hostVanilla, out var hostVanillaInv);
			var delta = hostVanillaInv * smoothedHost;

			for (var i = 0; i < attached.Length; i++)
			{
				var renderEffect = _particleManagerGet(attached[i].EffectId);
				if (renderEffect == null)
				{
					continue;
				}

				var smoothed = attached[i].VanillaMatrix * delta;

				var state = (MyParticleEffectState)_particleStateField.GetValue(renderEffect);
				state.WorldMatrix = smoothed;
				state.TransformDirty = true;

				_particleUpdateState(renderEffect, ref state);
			}
		}

		// Per-render-frame override for unparented MyLights tied to a
		// smoothed entity (headlamp, tool effect light). Mirror of the
		// entity-pose path above but written to each light's MyActor
		// directly: MyLight rides on the UpdateRenderLight message, which
		// has no PostponedUpdate equivalent, and
		// MyLightComponent.UpdateData would re-acquire the reflector
		// texture each frame. SetMatrix is what MyLightComponent.UpdateData
		// ends up calling on the unparented light anyway (Owner.SetMatrix
		// of CreateWorld for spots, CreateTranslation for points), so we
		// write the same matrix shape directly. Skipped for parented
		// lights at capture time — for those, a world-space write would be
		// silently overwritten by MyInstance.UpdateWorldMatrix the next
		// frame.
		private static void ApplyAttachedLightSmoothing(AttachedLightSnapshot[] lights, double alpha)
		{
			if (lights == null || lights.Length == 0)
			{
				return;
			}

			var alphaF = (float)alpha;
			for (var i = 0; i < lights.Length; i++)
			{
				var actor = MyIDTracker<MyActor>.FindByID(lights[i].LightRenderObjectId);
				if (actor == null)
				{
					// Light despawned between sim capture and this render
					// frame — e.g. character died, tool was holstered, or
					// the contact-point effect ended. Nothing to override;
					// the next sim tick will rebuild the snapshot or evict
					// the entry.
					continue;
				}

				Vector3D.Lerp(ref lights[i].PrevPosition, ref lights[i].CurrPosition, alpha, out var smoothPos);

				MatrixD smoothMatrix;
				if (lights[i].IsSpot)
				{
					// Reconstruct (forward, up) from the slerped quaternion.
					// CreateWorld with the rotation matrix's basis vectors
					// keeps the cone aligned the same way MyLight.UpdateLight
					// built m_matrix on the sim thread.
					var smoothOrientation = Quaternion.Slerp(lights[i].PrevOrientation,
						lights[i].CurrOrientation, alphaF);
					var rotMat = Matrix.CreateFromQuaternion(smoothOrientation);
					smoothMatrix = MatrixD.CreateWorld(smoothPos, rotMat.Forward, rotMat.Up);
				}
				else
				{
					// Point light — engine writes CreateTranslation(Position).
					// Match it exactly so any code that reads Owner.Matrix
					// (e.g. cull-tree projection) sees the same identity
					// rotation it would in vanilla.
					smoothMatrix = MatrixD.CreateTranslation(smoothPos);
				}

				actor.SetMatrix(ref smoothMatrix);
			}
		}

		// MyCubeBuilder.Draw runs at sim rate (60 Hz) on the update thread,
		// so the per-entity preview matrices the engine queues each tick are
		// stale across the multiple render frames between sim ticks. The
		// captured (renderObjectId, vanillaWorldMatrix) pairs in
		// PlacementPreviewCapture come from the most recent sim tick paired
		// with the vanilla camera that produced them; per render frame we
		// post-multiply each by inverse(vanilla_cam) * smoothed_cam — same
		// formula as the on-tick prefix would have applied, but driven at
		// render rate.
		private static void ApplyPlacementPreviewSmoothing(ref MatrixD smoothedCameraWorld)
		{
			var snapshot = PlacementPreviewCapture.Read();
			if (snapshot?.Entries == null || snapshot.Entries.Length == 0)
			{
				return;
			}

			var vanillaCameraWorld = snapshot.VanillaCameraWorld;
			MatrixD.Invert(ref vanillaCameraWorld, out var vanillaInv);
			var delta = vanillaInv * smoothedCameraWorld;

			var entries = snapshot.Entries;
			for (var i = 0; i < entries.Length; i++)
			{
				var id = entries[i].RenderEntityId;
				if (id == uint.MaxValue)
				{
					continue;
				}

				var smoothed = entries[i].VanillaWorldMatrix * delta;

				_entityUpdateMessage.ID = id;
				_entityUpdateMessage.Data.WorldMatrix = smoothed;
				_entityUpdateMessage.Data.HasWorldMatrix = true;

				_savePostponedUpdate(_entityUpdateMessage);
				_applyPostponedUpdate(id);
			}
		}
	}
}
