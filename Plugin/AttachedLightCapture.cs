using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Lights;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRageMath;

namespace SmoothFrames
{
	/// <summary>
	///     Sim-thread helper that snapshots every <see cref="MyLight"/>
	///     attached to a smoothed entity per tick. The render thread later
	///     lerps the captured (prev, curr) pose at the same alpha as the
	///     camera and writes the smoothed pose directly to the light's
	///     <see cref="VRage.Render.Scene.MyActor"/> via
	///     <see cref="VRage.Render.Scene.MyActor.SetMatrix"/>, bypassing the
	///     queued <c>UpdateRenderLight</c> path.
	///
	///     Motivation: when a host entity is smoothed at render rate but a
	///     light rigidly tied to it stays at sim rate, the lit surface
	///     smears across inter-tick frames. Most lights in SE parent through
	///     the cull tree (block lights via per-cell cull objects, jetpack
	///     thrust via the character's cull parent) and follow their
	///     smoothed parent automatically. The unparented exceptions need
	///     manual smoothing — currently:
	///     <list type="bullet">
	///         <item>
	///             <see cref="Sandbox.Game.Components.MyRenderComponentCharacter"/>
	///             <c>.m_light</c> — the character headlamp spotlight.
	///         </item>
	///         <item>
	///             <see cref="MyEngineerToolBase"/><c>.m_toolEffectLight</c>
	///             — the welder/grinder contact-point point light.
	///         </item>
	///     </list>
	/// </summary>
	internal static class AttachedLightCapture
	{
		// MyRenderComponentCharacter.m_light. Set once by InitLight at
		// character spawn; never reassigned, so we only need to read the
		// field. Resolved by name because m_light is private.
		private static readonly System.Type _characterRenderComponentType =
			AccessTools.TypeByName("Sandbox.Game.Components.MyRenderComponentCharacter")
			?? throw Errors.NotResolved("Sandbox.Game.Components.MyRenderComponentCharacter");
		private static readonly FieldInfo _characterLightField =
			AccessTools.Field(_characterRenderComponentType, "m_light")
			?? throw Errors.NotResolved("MyRenderComponentCharacter.m_light");

		// MyEngineerToolBase.m_toolEffectLight. Single slot swapped between
		// the primary and secondary tool action; null when the tool isn't
		// actively welding/grinding.
		private static readonly FieldInfo _toolEffectLightField =
			AccessTools.Field(typeof(MyEngineerToolBase), "m_toolEffectLight")
			?? throw Errors.NotResolved("MyEngineerToolBase.m_toolEffectLight");

		// Per-light prev-tick pose, keyed by render-object id. Lights move
		// between hosts as tools are picked up / put down, but the render
		// id is unique to the light's lifetime — keying on it lets the
		// prev/curr pairing survive holster cycles without bleeding pose
		// across light teardowns. Eviction below drops entries for any
		// render id not seen this tick.
		private static readonly Dictionary<uint, Pose> _lastSeenPose = new Dictionary<uint, Pose>();

		// Render ids visited during the current sim-tick collection pass.
		// Cleared at BeginTick and queried by EvictStale.
		private static readonly HashSet<uint> _seenLightIds = new HashSet<uint>();

		private static readonly List<uint> _evictScratch = new List<uint>();

		private struct Pose
		{
			public Vector3D Position;
			public Quaternion Orientation;
		}

		/// <summary>
		///     Reset the per-tick "seen" set. Called by
		///     <c>CameraCapture.CollectSmoothedEntities</c> at the start of
		///     each pass so <see cref="EvictStale"/> can identify lights
		///     that no smoothed entity captured this tick.
		/// </summary>
		public static void BeginTick()
		{
			_seenLightIds.Clear();
		}

		/// <summary>
		///     Append every attached <see cref="MyLight"/> on
		///     <paramref name="entity"/> to <paramref name="dest"/>. Caller
		///     is expected to have cleared the list. Skips lights whose
		///     render-object id hasn't been assigned yet, and lights that
		///     are parented in the renderer (those follow their parent
		///     actor automatically — a world-space override would be
		///     overwritten by <c>MyInstance.UpdateWorldMatrix</c> next
		///     frame).
		/// </summary>
		public static void Collect(MyEntity entity, List<AttachedLightSnapshot> dest)
		{
			var renderComp = entity.Render;
			if (renderComp != null
				&& _characterRenderComponentType.IsInstanceOfType(renderComp)
				&& _characterLightField.GetValue(renderComp) is MyLight headlamp)
			{
				AddIfPresent(headlamp, dest);
			}

			if (entity is MyEngineerToolBase tool
				&& _toolEffectLightField.GetValue(tool) is MyLight toolLight)
			{
				AddIfPresent(toolLight, dest);
			}
		}

		/// <summary>
		///     Drop prev-pose entries for lights that no smoothed entity
		///     captured this tick. Same shape as
		///     <c>CameraCapture.EvictStalePrevPoses</c>; called after the
		///     per-tick collection so a re-visited light keeps its prev
		///     pose.
		/// </summary>
		public static void EvictStale()
		{
			_evictScratch.Clear();
			foreach (var key in _lastSeenPose.Keys)
			{
				if (!_seenLightIds.Contains(key))
				{
					_evictScratch.Add(key);
				}
			}

			foreach (var key in _evictScratch)
			{
				_lastSeenPose.Remove(key);
			}
		}

		private static void AddIfPresent(MyLight light, List<AttachedLightSnapshot> dest)
		{
			var renderId = light.RenderObjectID;
			if (renderId == uint.MaxValue)
			{
				return;
			}

			if (light.ParentID != uint.MaxValue)
			{
				return;
			}

			var isSpot = light.ReflectorOn;
			var currPosition = light.Position;
			Quaternion currOrientation;

			if (isSpot)
			{
				// Build orientation from (forward, up). MatrixD.CreateWorld
				// with a zero translation yields a pure rotation matrix —
				// the same basis MyLight.UpdateLight uses for the actor
				// matrix when the light has no parent. Slerping the
				// quaternion lets the spotlight cone track the smoothed
				// host between sim ticks even during a fast turn.
				var dir = light.ReflectorDirection;
				var up = light.ReflectorUp;
				var rotMat = MatrixD.CreateWorld(Vector3D.Zero, dir, up);
				currOrientation = Quaternion.CreateFromRotationMatrix(rotMat);
			}
			else
			{
				// Point light — engine writes CreateTranslation(Position)
				// for the actor matrix, so orientation is unused. Identity
				// keeps the snapshot uniform without affecting the
				// render-side matrix (which branches on IsSpot).
				currOrientation = Quaternion.Identity;
			}

			_seenLightIds.Add(renderId);

			if (!_lastSeenPose.TryGetValue(renderId, out var prev))
			{
				// First tick we've seen this light — fall back to no motion
				// until we have two snapshots.
				prev.Position = currPosition;
				prev.Orientation = currOrientation;
			}

			_lastSeenPose[renderId] = new Pose
			{
				Position = currPosition,
				Orientation = currOrientation
			};

			dest.Add(new AttachedLightSnapshot
			{
				LightRenderObjectId = renderId,
				IsSpot = isSpot,
				PrevPosition = prev.Position,
				PrevOrientation = prev.Orientation,
				CurrPosition = currPosition,
				CurrOrientation = currOrientation
			});
		}
	}

	/// <summary>
	///     Per-tick snapshot of one attached light's pose. Carried inline on
	///     <see cref="SmoothedEntity"/> alongside attached particles. Spot
	///     lights use both position and orientation; point lights use
	///     position only and leave orientation as identity (the render-side
	///     apply branches on <see cref="IsSpot"/>).
	/// </summary>
	internal struct AttachedLightSnapshot
	{
		public uint LightRenderObjectId;
		public bool IsSpot;
		public Vector3D PrevPosition;
		public Quaternion PrevOrientation;
		public Vector3D CurrPosition;
		public Quaternion CurrOrientation;
	}
}
