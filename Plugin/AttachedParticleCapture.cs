using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageRender.Messages;

namespace SmoothFrames
{
	/// <summary>
	///     Sim-thread helper that discovers every active, unparented
	///     <see cref="MyParticleEffect"/> reachable from an entity instance —
	///     direct fields, fields of nested composition objects (e.g.
	///     <c>MyDrillBase</c>, <c>MyGunBase</c>), and elements of collection
	///     fields whose element type carries effect references (e.g.
	///     <c>MyGunBase.ActiveLoopEffects[].Effect</c>) — and emits an
	///     <see cref="AttachedParticle"/> for each one. Read on the sim thread
	///     by <see cref="CameraCapture"/> while building a
	///     <see cref="SmoothedEntity"/>; the render thread later applies a
	///     rigid pose correction per captured effect so it tracks the smoothed
	///     entity instead of staying at its baked-at-sim world coords. See
	///     <see cref="SmoothedEntity.AttachedParticles"/> for the rationale.
	/// </summary>
	internal static class AttachedParticleCapture
	{
		// MyParticleEffect.m_state holds the renderer-facing state struct,
		// whose ParentID determines which path the GPU emitter takes for that
		// effect. ParentID == uint.MaxValue means the emitter consumes the
		// state's WorldMatrix as a world position directly; any other ID makes
		// the emitter transform that matrix by MyActor.WorldMatrix of the
		// parent — i.e. the renderer auto-follows our smoothed parent and our
		// override would corrupt the local-space position. Filter out the
		// non-uint.MaxValue case so the rest of the pipeline only ever sees
		// effects that genuinely need our manual smoothing.
		private static readonly FieldInfo _stateField = AccessTools.Field(typeof(MyParticleEffect), "m_state")
			?? throw Errors.NotResolved("MyParticleEffect.m_state");

		// Per-runtime-type cached accessor lists. Built lazily on first
		// encounter of each entity type by walking instance fields with a
		// bounded depth — handles direct MyParticleEffect fields, fields of
		// composition objects (welder flame, tool sparks, drill dust/spark),
		// and fields holding collections of objects with MyParticleEffect
		// references (gun loop effects). Modded weapon/block types that follow
		// these patterns get covered without a hand-curated entry. Indexed by
		// the entity's GetType() so subclasses pick up their own field set
		// rather than the base class's.
		private static readonly Dictionary<Type, Accessor[]> _accessorsByType =
			new Dictionary<Type, Accessor[]>();

		private static readonly Accessor[] _empty = Array.Empty<Accessor>();

		// Recursion bound on the field walk. The deepest known case
		// (gun → MyGunBase → ActiveLoopEffects → WeaponEffect → Effect) bottoms
		// out at depth 3, and anything deeper risks pulling in shared engine
		// state via component back-references that aren't "attached to this
		// entity" in any meaningful sense.
		private const int MaxDepth = 3;

		private delegate void Accessor(object instance, List<AttachedParticle> dest);

		/// <summary>
		///     Append every active, unparented attached effect on
		///     <paramref name="entity"/> to <paramref name="dest"/>. Caller is
		///     expected to have cleared the list. Stopped, null, or render-side
		///     parented effects are skipped — the override path needs a live
		///     <c>MyParticleEffect.Id</c> and only does the right thing when
		///     <c>state.ParentID == uint.MaxValue</c>.
		/// </summary>
		public static void Collect(MyEntity entity, List<AttachedParticle> dest)
		{
			if (entity == null)
			{
				return;
			}

			var accessors = GetAccessors(entity.GetType());
			for (var i = 0; i < accessors.Length; i++)
			{
				accessors[i](entity, dest);
			}
		}

		private static Accessor[] GetAccessors(Type type)
		{
			if (_accessorsByType.TryGetValue(type, out var cached))
			{
				return cached;
			}

			var visiting = new HashSet<Type>();
			var built = BuildAccessors(type, depth: 0, visiting);
			_accessorsByType[type] = built;
			return built;
		}

		private static Accessor[] BuildAccessors(Type type, int depth, HashSet<Type> visiting)
		{
			if (depth > MaxDepth || !visiting.Add(type))
			{
				return _empty;
			}

			try
			{
				var list = new List<Accessor>();

				// Walk the inheritance chain explicitly; GetFields without
				// DeclaredOnly omits private fields on base classes (e.g.
				// MyEngineerToolBase.m_toolEffect when the runtime type is
				// MyWelder).
				var t = type;
				while (t != null && t != typeof(object))
				{
					foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public |
					                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						BuildOneAccessor(field, depth, visiting, list);
					}
					t = t.BaseType;
				}

				return list.Count == 0 ? _empty : list.ToArray();
			}
			finally
			{
				visiting.Remove(type);
			}
		}

		private static void BuildOneAccessor(FieldInfo field, int depth, HashSet<Type> visiting,
			List<Accessor> list)
		{
			var fieldType = field.FieldType;
			var f = field;

			if (fieldType == typeof(MyParticleEffect))
			{
				list.Add((instance, dest) => AddIfActiveUnparented(f.GetValue(instance) as MyParticleEffect, dest));
				return;
			}

			if (TryGetEnumerableElementType(fieldType, out var elementType))
			{
				if (elementType == typeof(MyParticleEffect))
				{
					list.Add((instance, dest) =>
					{
						if (!(f.GetValue(instance) is IEnumerable enumerable))
						{
							return;
						}
						foreach (var element in enumerable)
						{
							AddIfActiveUnparented(element as MyParticleEffect, dest);
						}
					});
					return;
				}

				if (!ShouldRecurseInto(elementType))
				{
					return;
				}
				var elementAccessors = BuildAccessors(elementType, depth + 1, visiting);
				if (elementAccessors.Length == 0)
				{
					return;
				}
				list.Add((instance, dest) =>
				{
					if (!(f.GetValue(instance) is IEnumerable enumerable))
					{
						return;
					}
					foreach (var element in enumerable)
					{
						if (element == null)
						{
							continue;
						}
						for (var i = 0; i < elementAccessors.Length; i++)
						{
							elementAccessors[i](element, dest);
						}
					}
				});
				return;
			}

			if (!ShouldRecurseInto(fieldType))
			{
				return;
			}
			var nestedAccessors = BuildAccessors(fieldType, depth + 1, visiting);
			if (nestedAccessors.Length == 0)
			{
				return;
			}
			list.Add((instance, dest) =>
			{
				var nested = f.GetValue(instance);
				if (nested == null)
				{
					return;
				}
				for (var i = 0; i < nestedAccessors.Length; i++)
				{
					nestedAccessors[i](nested, dest);
				}
			});
		}

		// Whitelist by assembly + blacklist by base class. The whitelist keeps
		// the walk inside the engine's own composition graphs (and skips
		// System.* types like Action<T> closures). The blacklist prevents
		// expansion into entity components and other entities — both reachable
		// from many composition objects (e.g. MyGunBase.m_user is the holder
		// MyEntity) and neither "attached to this entity" in the sense the
		// smoothing path needs.
		private static bool ShouldRecurseInto(Type t)
		{
			if (t == null || t.IsPrimitive || t.IsEnum || t.IsValueType)
			{
				return false;
			}
			if (t == typeof(string) || t == typeof(object))
			{
				return false;
			}
			if (t == typeof(MyParticleEffect))
			{
				return false;
			}
			if (typeof(Delegate).IsAssignableFrom(t))
			{
				return false;
			}
			if (typeof(MyEntity).IsAssignableFrom(t))
			{
				return false;
			}
			if (typeof(MyEntityComponentBase).IsAssignableFrom(t))
			{
				return false;
			}
			var asm = t.Assembly.GetName().Name;
			return asm == "Sandbox.Game" || asm == "Sandbox.Common" ||
			       asm == "VRage" || asm == "VRage.Game" ||
			       asm == "VRage.Render" || asm == "VRage.Render11" ||
			       asm == "SpaceEngineers.Game";
		}

		private static bool TryGetEnumerableElementType(Type type, out Type element)
		{
			if (type.IsArray)
			{
				element = type.GetElementType();
				return element != null;
			}

			// Match the most specific IEnumerable<T> the type implements.
			// Dictionary<TKey, TValue> reports KeyValuePair<,> here; that's a
			// value type, so the element-type recursion guard skips it.
			foreach (var iface in type.GetInterfaces())
			{
				if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
				{
					element = iface.GetGenericArguments()[0];
					return true;
				}
			}

			element = null;
			return false;
		}

		private static void AddIfActiveUnparented(MyParticleEffect fx, List<AttachedParticle> dest)
		{
			if (fx == null || fx.IsStopped)
			{
				return;
			}
			var state = (MyParticleEffectState)_stateField.GetValue(fx);
			if (state.ParentID != uint.MaxValue)
			{
				return;
			}
			dest.Add(new AttachedParticle
			{
				EffectId = fx.Id,
				VanillaMatrix = state.WorldMatrix
			});
		}
	}
}
