using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.Graphics;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SmoothFrames
{
	/// <summary>
	///     Smooths the artificial horizon (the level-line + altitude overlay
	///     drawn under the crosshair when piloting in natural gravity). Vanilla
	///     <see cref="MyGuiScreenHudSpace"/><c>.DrawArtificialHorizonAndAltitude</c>
	///     emits a <see cref="MyRenderMessageDrawSpriteExt"/> whose
	///     destination position and rotation are computed each sim tick from
	///     the cube block's <c>WorldMatrix</c> (Forward/Right/Up dotted with
	///     the gravity vector at the block's position) and the sim-tick
	///     value of <c>MyHud.Crosshair.Position</c>. Both inputs are
	///     sim-tick stale, so at high refresh the level line stutters
	///     every 16.67 ms while the world geometry slides smoothly.
	///
	///     Fix shape: capture the inputs (gravity, the seat's world anchor
	///     for the crosshair projection, and the <c>0.35f / 0.45f</c>
	///     viewport multiplier) in a Prefix on
	///     <c>DrawArtificialHorizonAndAltitude</c>; tag the resulting
	///     <see cref="MyRenderMessageDrawSpriteExt"/> in our shared
	///     <see cref="MyRenderProxy"/><c>.EnqueueMessage</c> postfix; per
	///     render frame, recompute pitch (`gravity · forward_smoothed`),
	///     roll (`atan2(right_smoothed · gravity, up_smoothed · gravity)`),
	///     and the screen-space anchor from the smoothed crosshair
	///     projection, then write the updated values back to
	///     <c>DestinationRectangle</c>'s position and <c>RightVector</c>.
	///
	///     The smoothed cockpit basis comes from applying the piloted-ship
	///     correction (<c>inv(grid_curr) · smoothed_grid</c>, from
	///     <see cref="BillboardSmoothing"/>) to the cube block's sim-T world
	///     matrix — *not* from the grid's smoothed basis directly. The grid's
	///     Forward/Right/Up axes are fixed at grid construction time and bear
	///     no relationship to where the cockpit is pointed; using them here
	///     produced a ~500 px vertical offset and ~30° roll error on any
	///     non-trivially-oriented grid. The smoothed crosshair position is
	///     recomputed inline (same math as
	///     <c>HudMarkerSmoothing.ApplyCrosshairMutation</c>) rather than
	///     reading a stashed value, to keep this file independent of
	///     HUD-marker internals.
	///
	///     There's at most one level-line emission per sim tick, so a
	///     single static slot for the tagged message + context suffices —
	///     no per-instance dictionary.
	/// </summary>
	internal static class ArtificialHorizonSmoothing
	{
		internal sealed class LevelLineContext
		{
			// Negated normalized gravity vector at the ship's sim-T position.
			// Captured at sim emission. Approximately constant over an
			// inter-tick (the ship moves at most a few meters per tick, and
			// natural gravity varies slowly with position), so re-sampling
			// on the render thread isn't worth the cost.
			public Vector3 GravityUp;

			// `0.35f * Viewport.Height` (1st-person) or
			// `0.45f * Viewport.Height` (3rd-person). Captured because
			// viewport height is sampled from the main camera's state at
			// sim time and we don't want to re-read it on the render thread.
			public float NumSix;

			// Crosshair anchor world coord at sim T = `seat.GetPosition() +
			// 1000 * seat.Forward` — the same point `PatchMyHudCrosshairDraw`
			// captures. The level-line position follows the smoothed
			// crosshair (anchors off `MyHud.Crosshair.Position` in vanilla),
			// so we re-project this through the piloted-ship correction +
			// smoothed camera each render frame to derive a smoothed anchor.
			public Vector3D SeatAnchorWorldPos;

			// `MyGuiManager.GetSafeFullscreenRectangle()`'s width/height,
			// captured at sim time. The anchor formula in
			// `DrawArtificialHorizonAndAltitude` is
			// `Crosshair.Position / hudSize * safeFullscreen`; we apply the
			// same scale on the render thread.
			public Vector2 SafeFullscreenSize;

			// `MyHud.Crosshair.Position` at sim T (X normalized [0..1], Y in
			// HUD-pixel space — see MyHudCrosshair storage doc). Used to
			// compute the per-render-frame shift for the altitude text
			// (`smoothedCrosshair − VanillaCrosshair`) since the altitude
			// text's anchor formula in `DrawArtificialHorizonAndAltitude` is
			// a linear function of `Crosshair.Position` and the constant
			// `+0.03` Y offset cancels in the delta.
			public Vector2 VanillaCrosshairPosition;

			// Captured at OnMessageEnqueued time — engine's vanilla emission
			// values. Used as deltas-from references and to recover the
			// texture-quad size for re-anchoring without touching the
			// message's Size field.
			public Vector2 OriginalDestinationPosition;
			public Vector2 OriginalDestinationSize;
			public Vector2 OriginalRightVector;

			// Altitude text's vanilla ScreenCoord (pixels). Captured at
			// OnMessageEnqueued time when the level-line context is set and
			// a DrawString message is enqueued. Per render frame the text
			// shifts by `(smoothedCrosshair − VanillaCrosshair) ×
			// safeFullscreen / hudSize` from this reference.
			public Vector2 OriginalAltitudeTextScreenCoord;

			// Cube-block-of-the-ship-controller's vanilla world matrix at
			// sim T. Vanilla `DrawArtificialHorizonAndAltitude` dots gravity
			// against this block's Forward/Right/Up — *not* the grid's. For
			// a cockpit whose local-to-grid orientation isn't identity (and
			// "isn't identity" is the typical case — the grid's forward axis
			// is fixed at grid construction and has no relationship to the
			// cockpit mount direction), the grid basis can be near-orthogonal
			// to the cockpit basis, producing a 500+ pixel error in the
			// level-line offset and ~30° error in the roll. Captured so the
			// render thread can apply the piloted-ship correction to recover
			// the cockpit's *smoothed* basis (`cockpit_smoothed = cockpit_T
			// · correction`, XNA row convention).
			public MatrixD CockpitWorldVanilla;
		}

		[ThreadStatic] private static LevelLineContext _currentContext;

		private static MyRenderMessageDrawSpriteExt _pendingSpriteMessage;
		private static MyRenderMessageDrawString _pendingTextMessage;
		private static LevelLineContext _pendingContext;

		public static void SetContext(LevelLineContext ctx)
		{
			_currentContext = ctx;
		}

		public static void ClearContext()
		{
			_currentContext = null;
		}

		// Render-thread postfix on MyMessagePool.Return. Drops the
		// disposed message's pending pointer BEFORE the pool can hand
		// the instance back to sim's MessagePool.Get on a later tick.
		// Closes the same 1-frame swap race the HUD-marker fix did:
		// without it, sim's next-tick MessagePool.Get on this pooled
		// instance can hand back a MyRenderMessageDrawString still
		// referenced by _pendingTextMessage from a past altitude
		// emission, and Build Info's overlay text (which also routes
		// through MyRenderProxy.DrawString) can land on that instance.
		// Between Build Info writing the new content's ScreenCoord and
		// our EnqueueMessage postfix nulling the stale pointer, the
		// render thread's MutateLevelLine could fire and overwrite
		// Build Info's ScreenCoord with the altitude's smoothed
		// position — visible as a 1-frame swap of altitude text and
		// Build Info content. Same shape for the sprite-ext path
		// (horizon quad).
		public static void OnMessagePoolReturn(MyRenderMessageBase message)
		{
			if (_pendingSpriteMessage == null && _pendingTextMessage == null)
			{
				return;
			}
			if (_pendingSpriteMessage != null && ReferenceEquals(message, _pendingSpriteMessage))
			{
				_pendingSpriteMessage = null;
			}
			if (_pendingTextMessage != null && ReferenceEquals(message, _pendingTextMessage))
			{
				_pendingTextMessage = null;
			}
			if (_pendingSpriteMessage == null && _pendingTextMessage == null)
			{
				_pendingContext = null;
			}
		}

		public static void OnMessageEnqueued(MyRenderMessageBase message)
		{
			if (_currentContext == null)
			{
				// Non-level-line emission. PatchMyMessagePoolReturn already
				// cleared any stale pending pointer when this pooled
				// instance was disposed back to the pool, so nothing to do
				// here.
				return;
			}

			if (message is MyRenderMessageDrawSpriteExt spriteExt)
			{
				_currentContext.OriginalDestinationPosition = spriteExt.DestinationRectangle.Position;
				_currentContext.OriginalDestinationSize = spriteExt.DestinationRectangle.Size;
				_currentContext.OriginalRightVector = spriteExt.RightVector;
				_pendingSpriteMessage = spriteExt;
				_pendingContext = _currentContext;
				return;
			}

			if (message is MyRenderMessageDrawString str)
			{
				// Inside DrawArtificialHorizonAndAltitude only the altitude
				// text emits a DrawString. Tag it so the per-render-frame
				// mutation can shift it alongside the smoothed crosshair.
				_currentContext.OriginalAltitudeTextScreenCoord = str.ScreenCoord;
				_pendingTextMessage = str;
				_pendingContext = _currentContext;
			}
		}

		// Render thread, called from RenderFrameSmoothing.RunFrame after the
		// HUD-marker tracked-sprite refresh has run. Same lifecycle as the
		// marker mutation: messages persist between sim ticks (the engine
		// queues sprite-ext + draw-string messages once per HUD draw and
		// our mutation lands before RenderMainSprites consumes them each
		// render frame), so we re-apply the smoothed math each frame using
		// the latest CameraHistory alpha. Handles both the horizon sprite
		// (DestinationRectangle position + RightVector rotation) and the
		// altitude text (ScreenCoord shift) under one shared
		// smoothed-crosshair derivation.
		public static void MutateLevelLine()
		{
			var ctx = _pendingContext;
			if (ctx == null)
			{
				return;
			}

			if (!TryProjectSmoothedCrosshair(ctx.SeatAnchorWorldPos, out var smoothedCrosshairNormalized))
			{
				return;
			}

			var hudSize = MyGuiManager.GetHudSize();
			var smoothedCrosshairPosUnits = new Vector2(
				smoothedCrosshairNormalized.X,
				smoothedCrosshairNormalized.Y * hudSize.Y);

			var spriteMsg = _pendingSpriteMessage;
			if (spriteMsg != null)
			{
				MutateHorizonSprite(spriteMsg, ctx, smoothedCrosshairPosUnits, hudSize);
			}

			var textMsg = _pendingTextMessage;
			if (textMsg != null)
			{
				MutateAltitudeText(textMsg, ctx, smoothedCrosshairPosUnits, hudSize);
			}
		}

		private static void MutateHorizonSprite(
			MyRenderMessageDrawSpriteExt msg,
			LevelLineContext ctx,
			Vector2 smoothedCrosshairPosUnits,
			Vector2 hudSize)
		{
			// Need the piloted-ship correction (`inv(grid_curr) · smoothed_grid`)
			// to advance the cockpit's vanilla pose to the smoothed-grid pose.
			// Critical not to short-circuit by reading the grid's smoothed
			// basis directly: the grid's Forward/Right/Up axes have no
			// relationship to the cockpit's, and substituting them produced
			// a ~500 px vertical offset error and ~30° roll error on any
			// ship whose cockpit local-to-grid rotation isn't identity
			// (which is the common case — the grid's forward is whatever
			// direction it was when the first block was placed).
			if (!BillboardCorrections.TryGetPilotedShipCorrection(out var correction))
			{
				return;
			}

			// Cube-block-smoothed basis: cube_block_smoothed = cube_block_T
			// · correction (XNA row convention; correction is rigid, so
			// this preserves the orthonormal basis the gravity dot products
			// need). Vector3 narrowing matches vanilla's
			// `(Vector3) WorldMatrix.Forward` etc.
			var cockpitSmoothed = ctx.CockpitWorldVanilla * correction;
			var fSmoothed = (Vector3)cockpitSmoothed.Forward;
			var rSmoothed = (Vector3)cockpitSmoothed.Right;
			var uSmoothed = (Vector3)cockpitSmoothed.Up;

			// Pitch and roll, smoothed. Same formulas as
			// `MyGuiScreenHudSpace.DrawArtificialHorizonAndAltitude`.
			var v = ctx.GravityUp;
			var num4Smoothed = (double)Vector3.Dot(v, fSmoothed);
			var num7Smoothed = num4Smoothed * ctx.NumSix;

			var dotRight = Vector3.Dot(rSmoothed, v);
			var dotUp = Vector3.Dot(uSmoothed, v);
			var rollLenSq = dotRight * dotRight + dotUp * dotUp;
			var num8Smoothed = rollLenSq > 1e-5
				? Math.Atan2(dotUp, dotRight)
				: 0.0;

			// vector = Crosshair.Position / hudSize * safeFullscreen
			// (vanilla anchor formula with smoothed inputs).
			var vectorSmoothed = new Vector2(
				smoothedCrosshairPosUnits.X / hudSize.X * ctx.SafeFullscreenSize.X,
				smoothedCrosshairPosUnits.Y / hudSize.Y * ctx.SafeFullscreenSize.Y);

			// destination.Position = vector - texHalfSize + (0, num7).
			// Mirrors the vanilla emission with smoothed inputs.
			var halfSize = ctx.OriginalDestinationSize * 0.5f;
			var newDestPos = vectorSmoothed - halfSize + new Vector2(0f, (float)num7Smoothed);

			var rect = msg.DestinationRectangle;
			rect.Position = newDestPos;
			msg.DestinationRectangle = rect;

			// rightVector = (sin(num8), cos(num8)) — vanilla emission verbatim.
			msg.RightVector = new Vector2(
				(float)Math.Sin(num8Smoothed),
				(float)Math.Cos(num8Smoothed));
		}

		// Altitude text shifts by `(smoothedCrosshair − VanillaCrosshair) ×
		// safeFullscreen / hudSize` in pixel space — derived from the
		// vanilla altitude-text anchor formula (`Crosshair.Position * num2
		// / hudSize + (0, 0.03f)`, with `num2 ≈ Fullscreen/SafeFullscreen
		// ≈ 1`). The constant `+0.03` Y offset and the integer ratio
		// cancel in the delta, so the shift collapses to a pure
		// proportional change.
		private static void MutateAltitudeText(
			MyRenderMessageDrawString msg,
			LevelLineContext ctx,
			Vector2 smoothedCrosshairPosUnits,
			Vector2 hudSize)
		{
			var crosshairShiftHud = smoothedCrosshairPosUnits - ctx.VanillaCrosshairPosition;
			var hudXScale = hudSize.X > 0f ? hudSize.X : 1f;
			var hudYScale = hudSize.Y > 0f ? hudSize.Y : 1f;
			var shiftPixel = new Vector2(
				crosshairShiftHud.X / hudXScale * ctx.SafeFullscreenSize.X,
				crosshairShiftHud.Y / hudYScale * ctx.SafeFullscreenSize.Y);

			msg.ScreenCoord = ctx.OriginalAltitudeTextScreenCoord + shiftPixel;
		}

		// Same projection chain ApplyCrosshairMutation in HudMarkerSmoothing
		// runs: piloted-ship correction → view matrix from smoothed cam
		// pose → ProjectionMatrix from the latest sim-tick snapshot →
		// normalized HUD coords [0..1]. Replicated locally rather than
		// exposed from HudMarkerSmoothing to keep this patch independent.
		private static bool TryProjectSmoothedCrosshair(Vector3D seatAnchorWorld, out Vector2 normalized)
		{
			normalized = Vector2.Zero;

			if (!BillboardCorrections.TryGetPilotedShipCorrection(out var correction))
			{
				return false;
			}

			var worldCorrected = Vector3D.Transform(seatAnchorWorld, correction);

			if (!RenderFrameSmoothing.TryGetSmoothedPose(out var camPos, out var camRot))
			{
				return false;
			}

			if (!CameraHistory.TryGetPair(out _, out var curr))
			{
				return false;
			}

			var rotMatF = Matrix.CreateFromQuaternion(camRot);
			var worldMat = (MatrixD)rotMatF;
			worldMat.Translation = camPos;
			MatrixD.Invert(ref worldMat, out var viewMat);

			var posInView = Vector3D.Transform(worldCorrected, viewMat);
			var clip = Vector4D.Transform(posInView, (MatrixD)curr.ProjectionMatrix);

			if (posInView.Z > 0.0)
			{
				clip.X = -clip.X;
				clip.Y = -clip.Y;
			}

			if (clip.W == 0.0)
			{
				return false;
			}

			var x = clip.X / clip.W / 2.0 + 0.5;
			var y = -clip.Y / clip.W / 2.0 + 0.5;

			normalized = new Vector2((float)x, (float)y);
			return true;
		}
	}

	/// <summary>
	///     Sim-thread Prefix on
	///     <see cref="MyGuiScreenHudSpace"/><c>.DrawArtificialHorizonAndAltitude</c>.
	///     Captures the inputs the level-line math depends on (gravity up
	///     vector, viewport multiplier, safe fullscreen size, seat anchor
	///     world coord) and sets the
	///     <see cref="ArtificialHorizonSmoothing"/> context. The original
	///     method runs as normal — its
	///     <see cref="MyRenderProxy"/><c>.DrawSpriteExt</c> emission gets
	///     tagged by the shared <c>EnqueueMessage</c> postfix while the
	///     context is set; the Postfix clears the context.
	///
	///     Returns without setting context (and so without smoothing the
	///     level line) under the same gates vanilla applies — not piloting
	///     a ship controller, no controlled cube block, etc. Vanilla itself
	///     early-returns or skips the emission in those cases at
	///     <c>MyGuiScreenHudSpace</c>'s call site, so a no-op tag is the
	///     safe default.
	/// </summary>
	[HarmonyPatch(typeof(MyGuiScreenHudSpace), "DrawArtificialHorizonAndAltitude")]
	public static class PatchDrawArtificialHorizonAndAltitude
	{
		public static void Prefix()
		{
			var session = MySession.Static;
			var controlled = session?.ControlledEntity?.Entity;
			if (controlled == null)
			{
				return;
			}

			if (!(controlled is MyShipController shipController))
			{
				return;
			}

			var positionComp = shipController.PositionComp;
			if (positionComp == null)
			{
				return;
			}

			var anchorPos = positionComp.WorldMatrixRef.Translation;
			var gravityRaw = MyGravityProviderSystem.CalculateNaturalGravityInPoint(anchorPos);
			if (gravityRaw.LengthSquared() < 0.0025f)
			{
				// Vanilla `DrawArtificialHorizonAndAltitude` skips the
				// level-line draw when natural gravity is below this
				// threshold. No emission to tag, leave the context null.
				return;
			}

			var gravityUp = -gravityRaw;
			gravityUp.Normalize();

			var viewport = MySector.MainCamera.Viewport;
			var isThirdPerson =
				session.GetCameraControllerEnum() == VRage.Game.MyCameraControllerEnum.ThirdPersonSpectator;
			var numSix = (isThirdPerson ? 0.45f : 0.35f) * viewport.Height;

			var safeFullscreen = MyGuiManager.GetSafeFullscreenRectangle();

			var seatAnchorWorld = positionComp.GetPosition()
				+ 1000.0 * positionComp.WorldMatrixRef.Forward;

			ArtificialHorizonSmoothing.SetContext(new ArtificialHorizonSmoothing.LevelLineContext
			{
				GravityUp = gravityUp,
				NumSix = numSix,
				SeatAnchorWorldPos = seatAnchorWorld,
				SafeFullscreenSize = new Vector2(safeFullscreen.Width, safeFullscreen.Height),
				VanillaCrosshairPosition = MyHud.Crosshair.Position,
				CockpitWorldVanilla = positionComp.WorldMatrixRef
			});
		}

		public static void Postfix()
		{
			ArtificialHorizonSmoothing.ClearContext();
		}
	}
}
