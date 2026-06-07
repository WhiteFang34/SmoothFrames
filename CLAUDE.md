# SmoothFrames plugin development

## Vocabulary

These terms label distinct phenomena, not synonyms. Use them
consistently in code comments and the rest of this doc.

- **Ghost.** Multiple visible copies of an element because it
  draws at sim-tick poses while the rest of the scene moves smoothly
  at render rate — the canonical "4 copies at 240 FPS" artifact this
  plugin exists to fix.
- **Stutter.** The temporal cause underlying a ghost: element
  updating at sim rate (60 Hz, jumps every 16.67 ms) while
  surrounding geometry advances at render rate.
- **Wobble.** Residual oscillation of a partially-corrected element,
  typically motion-proportional — e.g. a billboard that's *almost*
  welded to a moving ship but still drifts a few cm per tick of
  velocity.
- **Flicker.** Momentary 1-frame visual pop. Distinct from the
  others — single transient blink, not a sustained pattern.
- **Swap.** A 1-frame content-identity flip — the visible slot
  of one element briefly renders another element's content.
  Distinct from flicker: flicker is the same element popping;
  swap is the wrong element in the slot. Typically a
  pool-slot-reuse symptom — a recycled handle (HUD message,
  sprite, billboard) is keyed against a slot another consumer
  has re-purposed this tick.

## Code style

- **Braces.** Allman; always brace bodies, even single-statement
  (IDE0011).
- **var preferred** when the RHS makes the type obvious. IDE0007
  only fires on casts and `new T()`, so explicit annotations like
  `MatrixD x = matrixValue` stay explicit by default.
- **Doc-comments.** `/// <summary>` for public types and methods.
  Plain `//` for everything else (private fields, internal logic
  rationale).
- **Comment wrap.** ~80 chars. Wider than that reads poorly for
  prose; the 120-char budget in `.editorconfig` is for code, not
  comments.
- **Comment intent.** Explain *why*, not *what*. The code shows
  what; the comment documents the rationale that took hours to
  derive (engine internals, race conditions, why this approach over
  another). Comments that just restate the code are noise.
- **No `[SuppressMessage]`.** Per-symbol suppressions are noise.
  If a third-party analyzer flags Harmony naming (`__instance`,
  `__result`, `__args`) or anything else, let users configure their
  own inspections — or suppress the rule globally in
  `.editorconfig` if it's wrong everywhere.

## Commit conventions

Reuse the verbs already in this repo's commit history — Added,
Updated, Fixed, Changed, Removed, Cleaned up. Terse one-line
subjects, no extended body unless asked.

## Investigation tactics

- **Treat the SE decompile as reference, not reading material.**
  Decompiled files run hundreds of thousands of lines each. Search
  for the specific method or field you need rather than scrolling
  through, and copy out only the answer — typically a
  `class.method` reference plus a few lines of code shape.
  Hypothesis-disconfirming lookups can run in parallel with
  current work; lookups whose answer drives the next step block.
- **Treat SE logs the same way.** The SE log
  (`%APPDATA%\SpaceEngineers\SpaceEngineers_*.log`) and the Pulsar
  plugin-load log (`%APPDATA%\Pulsar\Legacy\info.log`) are both
  long and mostly noise. Frame a specific question — "what
  entities does `SmoothFrames captured:` list when the player holds a
  grinder?", "do any render-IDs change across consecutive sim
  ticks?", "is there a Harmony patch failure on startup, and which
  patch class?" — and pull just the matching lines.
- **Land transient diagnostic logging without committing it.** Add
  whatever instrumentation is useful, ship it, capture the data, then
  strip it before committing the actual fix. If the diagnostic ends
  up being permanently useful, that's its own commit, separate from
  the fix.
- **Land a runtime A/B toggle for race-condition fixes.** When the
  bug being fixed is intermittent enough that the user's only signal
  is "I saw it" vs "I didn't see it," a "fix landed, run for a while,
  haven't seen it" verification collides with the same statistical
  fog that made the race hard to nail down in the first place — the
  absence of the symptom is consistent with either the fix working
  or the user being briefly lucky. Bind a hotkey that bypasses the
  fix at runtime — a `public volatile bool` defaulting to true, gated
  at the patch's entry point, flipped by an unused Ctrl+Shift+key
  chord; no config persistence. The user toggles it off, watches the
  symptom return; toggles back on, watches it stop. Same lifecycle
  as transient diagnostic logging — strip the toggle once verified,
  unless the bug class is recurrent enough to keep a config-gated
  escape hatch.
- **Verify what artifact you're actually fixing before designing
  the fix.** When candidate corrections don't move the symptom,
  suspect the artifact's identity rather than your math. Suppress
  candidate render passes or mod methods (a Harmony prefix returning
  `false` on `MyHighlight.Run`, an early-return in your own
  `RebakeLine`, etc.) to isolate which element actually wobbles.
  Twice in the depth-pulled-line investigation an artifact was
  misattributed: highlight-glow vs. wireframe-box, then vanilla vs.
  Build Info. Both led to long detours that an early-suppression
  test would have shortened.

## Harmony patches

- **`TargetMethod` for fast-fail.** Use `TargetMethod` even for
  fragile reflection lookups (private/nested types resolved by name).
  When a lookup returns `null`, `Harmony.PatchAll` aborts and every
  patch in the assembly fails to install — that loud failure is the
  desired signal when an SE update has broken reflection. The
  alternative (`TargetMethods` returning empty) silently skips just
  that patch and masks partial degradation. For lookups that may
  resolve to null, `?? throw Errors.NotResolved("…")` with the bare
  engine-surface descriptor — the helper supplies the `SmoothFrames:`
  prefix and `not resolved` suffix, while keeping the descriptor
  literal in the source so it stays grep-able:

  ```csharp
  private static readonly Type _someEngineType =
      AccessTools.TypeByName("Some.Engine.Internal+NestedStruct")
      ?? throw Errors.NotResolved("Some.Engine.Internal+NestedStruct");

  public static MethodBase TargetMethod()
  {
      return AccessTools.Method(_someEngineType, "Update", new[] { ... });
  }
  ```

- **Cache reflection lookups; fail loud at startup, log at runtime.**
  Resolve once — `static readonly` at type-init for surfaces that
  exist at assembly load (`?? throw Errors.NotResolved("…")`), or
  behind a flag-gated lazy helper for engine state populated
  post-load (postponed-update manager, particle-effects manager,
  frame-rate cap waiter — these log once via
  `MyLog.Default.WriteLineAndConsole` and fall back to vanilla, no
  throw). Don't re-walk `AccessTools` per call. Helper classes
  outside `[HarmonyPatch]` types need an explicit
  `RuntimeHelpers.RunClassConstructor` from `Plugin.Init` so their
  startup throws fire during `PatchAll` instead of mid-frame.
- **Let postfix exceptions bubble.** Don't wrap patch bodies in
  defensive `try/catch (Exception)`. Empirically these never fired
  (zero `SmoothFrames: … threw` log entries across real usage), and the
  conventional `_loggedError` once-per-session gate would have
  silently swallowed every occurrence after the first anyway. A
  bubbled exception crashes SE with a stack trace pointing at the
  bug — exactly the signal we want. Use `try/finally` only for
  cleanup that must run on the happy path too (e.g., clearing
  thread-local context).
- **Patch class naming.** Prefix Harmony-patch classes with `Patch`
  followed by the patched method name (`PatchEnqueueMessage`,
  `PatchDrawTexts`); when the bare method name would clash across
  patches (`Draw`, `Start`, `Update`, …), use
  `Patch<TypeName><MethodName>` instead (`PatchMyHudCrosshairDraw`,
  `PatchMyRenderEntityUpdate`). Keep patch classes thin — they
  resolve `TargetMethod` and dispatch into a topic-named class
  (`*Smoothing`, `*Capture`, `*Tracking`) that owns the actual
  logic and any shared state other modules read from. The split
  keeps each class to one concern: the patch only binds Harmony to
  the engine surface, the topic class is the API consumers see.

## Mod-specific recognition

The smoothing engine is mod-agnostic: it reasons about billboard
*types*, *materials*, and *geometry*, never about which mod emitted a
thing. The one exception is recognizing see-through-walls overlay
content a specific mod draws so it can be smoothed. That recognition is
split in two:

- **The engine talks only to a general recognizer**,
  `DepthPulledOverlay`. It owns the depth-pull constant (`NearRatio` and
  friends), the set of overlay materials, and the predicates the engine
  calls — `IsDepthPulledOverlayQuad` (bulk-emitted Custom quads: conveyor
  lines/arrows/boxes, LIDAR point cloud) and
  `IsCameraAnchoredViewfinderLine` (the LIDAR viewfinder square). The
  engine passes in its own frame state (camera pose, thresholds) and owns
  the correction mechanics.
- **The hard-coded mod internals live in a per-mod profile**, in its own
  `*<ModName>*`-named file (currently `BuildInfoOverlay`): the mod's
  material ids (folded into the recognizer's set via an `OverlayMaterials`
  array) and any geometric constants a correction needs (the LIDAR
  preview frustum's FOV/near-plane/aspect, behind
  `IsLidarPreviewSquareLine`). A second overlay mod gets its own profile
  wired into `DepthPulledOverlay` the same way.

Adding a new overlay mod is a code change for now, and only the *materials*
generalize cleanly: add them to a `*Overlay` profile's `OverlayMaterials`
and they fold into the recognizer's set automatically. A viewfinder test
or a different depth-pull ratio still means editing `DepthPulledOverlay`
itself — `IsCameraAnchoredViewfinderLine` is a single hard-coded profile
call, and `NearRatio` is one shared constant (the common recovery is
ratio-insensitive, but the moving-grid un-pull assumes it). A
runtime-configurable material list, and making the viewfinder/ratio
per-profile, were considered and deferred until more such mods surface.

Why the Custom quads need material identity at all (and can't be
recognized geometrically the way the dots and orient-carrying lines are):
their see-through technique — depth-pull to ~1% of camera distance, draw
with `BlendTypeEnum.PostPP` — is shared with screen HUD. PostPP is what
Rich HUD Master and Text HUD API draw their HUD with too; a depth-pulled
overlay sits so close to the camera it moves almost exactly like
camera-locked HUD; and the quads are square (arrows, boxes, short
segments), indistinguishable from HUD glyphs by shape. So the material is
the only reliable per-element signal. See "Considered and rejected" for
the full evaluation (and why inverting it — recognize HUD, depth-pull the
rest — is worse).

The **depth-pull ratio** (`DepthPulledOverlay.NearRatio`, 0.01) is a
general default, not a constant mirrored from one mod: the common recovery
(a `Complement` translation) is insensitive to the exact value (sub-mm
residual for any ratio in 0.001–0.05), and only the moving-grid un-pull
path (`InverseNearRatio`) is ratio-sensitive, where 0.01 covers the known
cases.

Rationale for the profile split:

- **Blast radius.** When a mod updates and breaks recognition, there's
  one file to check, and it's named for the mod.
- **Graceful degradation, not crashes.** Resolve a mod's identifiers in
  a way that no-ops when absent — material ids via
  `MyStringId.GetOrCompute` match the same intern-table entry the mod
  computes when loaded, and match nothing when it isn't, so the content
  simply falls back to the generic path.
- **The engine stays readable.** `BillboardSmoothing` says "if this is a
  recoverable overlay quad, recover it," not "if this material is
  `BuildInfo_Square` or …".

Geometric recognition (e.g. the near-camera Point path for conveyor dots,
the thin-line / tiny-radius depth-pull tests for the Measure tool) that
happens to target a mod but keys on no mod-specific data stays in the
engine — it's general, and works for any mod emitting the same shape.

**Corpus survey** (installed mods, June 2026). Confirming the split
generalizes: across every installed mod, Build Info is the *only* one that
bulk-emits depth-pulled `Custom` quads — the material-keyed case — via its
conveyor overlay and LIDAR point cloud. Two other depth-pull-overlay mods
(both Digi's) need no mod-specific code; the geometric paths catch them:

- **Leak Finder** draws its see-through leak overlay as depth-pulled
  `AddPointBillboard` points → the near-camera Point path.
- **Advanced Welding** draws an always-on-top weld-pad box as a thin
  (0.001) PostPP wireframe of lines, depth-pulled at ratio 0.05 → the
  thin-line path. Its 0.05 vs our 0.01 default corrects to a sub-mm
  residual — real-world confirmation that the common translation recovery
  is ratio-insensitive.

(The Water mod bulk-emits, but its quads are the world-anchored water
surface — not depth-pulled. The Rich HUD Framework family bulk-emits
screen HUD — camera-anchored, handled by the ordinal-lerp.) So the
material list stays Build-Info-only until some new mod bulk-emits
depth-pulled `Custom` quads; another mod's oriented points/lines, however
exotic, are already covered.

## Key engine internals

Engine surfaces this plugin reads, patches, or relies on. Names are
stable across SE patches (line numbers from the decompile aren't, so
they're omitted).

### Sim thread

- `MyCamera.UploadViewMatrixToRender` — runs once per sim tick.
  Postfixed to capture a camera snapshot.
- `MySession.Static.LocalCharacter` — local player's `MyCharacter`.
- `MyCharacter.CurrentWeapon` — held weapon as
  `IMyHandheldGunObject<MyDeviceBase>` (cast to `MyEntity`).
- `MySession.Static.ControlledEntity?.Entity` — current controlled
  entity; `GetTopMostParent()` reaches the ship grid when piloting.
- `MyEntity.Hierarchy.Children` — children pushed in via
  `MyHierarchyComponentBase.AddChild`. Does **not** contain subparts.
- `MyEntity.Subparts.Values` — populated by `MyEntity.RefreshModels`
  from `subpart_*` model dummies. The constructor sets
  `Hierarchy.Parent` directly, never `AddChild`, so subparts must be
  walked separately.
- `MyEntity.Render.RenderObjectIDs` / `MyEntity.WorldMatrix` /
  `MyEntity.EntityId` — render handles, world transform, and the
  `long` we key the prev-pose dictionary by.
- `MyCubeBuilder.Static` (singleton) / `MyCubeBuilder.CurrentGrid`
  (`protected internal`, reflected) — the active block builder and
  the grid the gizmo is snapped to (null = free-floating placement,
  e.g. dynamic mode in space). Read on the main thread to decide
  whether the gizmo is camera-anchored.
- `MyBlockBuilderRenderData.EndCollectingInstanceData(MatrixD
  gridWorldMatrix, bool useTransparency)` — public; called once per
  sim tick from `MyCubeBuilder.Draw`, iterates the active preview's
  render entities and dispatches per-entity world matrices via
  `MyRenderEntity.Update` → `MyRenderProxy.UpdateRenderObject`. The
  placement preview is *not* a `MyEntity` — its render-entity IDs
  live inside
  `MyBlockBuilderRenderData.MyEntities.RenderEntities`. Critical:
  this method runs at **sim rate (60 Hz) on the update thread**,
  *not* per render frame, even though it's reached through
  `MySandboxGame.PrepareForDraw → MyGuiSandbox.Draw →
  MySession.Draw → session-component Draw`; a Harmony prefix here
  modifies the matrix only once per sim tick and the renderer
  reuses it across every interpolated render frame in between.
  Use it as a sim-rate capture point (paired with a
  `MyRenderEntity.Update` postfix), not as a sub-tick override
  point.
- `MyBlockBuilderRenderData+MyRenderEntity` — private nested struct
  holding `public uint RenderEntityId`, `public Matrix LocalMatrix`
  (single-precision), and skin/color fields. Its `public static void
  Update(ref MyRenderEntity entity, ref MatrixD gridWorldMatrix,
  float transparency)` computes `value = entity.LocalMatrix *
  gridWorldMatrix` and dispatches `MyRenderProxy.UpdateRenderObject(
  entity.RenderEntityId, value)`. The struct is private to its
  containing class, so we resolve the type by name and read its
  fields via reflection; the Harmony patch matches the byref
  parameter list to bind to it.
- `MyCharacterWeaponPositionComponent.UpdateGraphicalWeaponPosition{1st,3rd}`
  — runs each sim tick from `MyCharacter.UpdateAfterSimulation` /
  `UpdateAnimation`; writes the held weapon's `WorldMatrix` and
  enqueues a render message. This is the sim-thread message that
  wins ordering against any queued render-thread update — the reason
  we go through the synchronous bypass instead.
- `MyRenderComponentCharacter.m_cullRenderId` — private `uint`,
  defaults to `uint.MaxValue`. Set at `AddRenderObjects` to a fresh
  `ManualCull` actor; the character's render object is parented to
  it via `SetParent(0, m_cullRenderId, Matrix.Identity)`. The
  overridden `InvalidateRenderObjects` writes matrix updates only
  to this id. We read it via reflection and treat it as the actor
  to smooth for any entity whose render component is
  `MyRenderComponentCharacter`.
- `MyRenderComponentCharacter.m_light` — private
  `Sandbox.Game.Lights.MyLight`; the character's headlamp
  spotlight, created in `InitLight` and never reassigned. Repositioned
  each sim tick by `UpdateLightPosition` from the head bone matrix
  (`Position = Transform(LightOffset, headMatrix)`,
  `ReflectorDirection = headMatrix.Forward`,
  `ReflectorUp = headMatrix.Up`). Unparented in vanilla, so its
  pose is absolute world coords — the cull-parent indirection that
  smooths the body does not reach it.
- `MyEngineerToolBase.m_toolEffectLight` — private `MyLight` for
  the welder/grinder contact-point glow. Single slot swapped between
  primary and secondary tool actions; null when not actively
  welding/grinding. Position-only (`ReflectorOn == false`); refreshed
  each sim tick from `GetEffectMatrix(0f, EffectType.Light)`.
- `MyLight.UpdateLight` — sim-side push of the light's pose +
  properties to the renderer via
  `MyRenderProxy.UpdateRenderLight(ref UpdateRenderLightData)`.
  Builds the actor matrix as `CreateWorld(Position,
  ReflectorDirection, ReflectorUp)` for spots, `CreateTranslation(
  Position)` for points. We mirror that exact matrix shape on the
  render thread for unparented lights captured by
  `AttachedLightCapture`.

### Render thread

- `MyRender11.ProcessMessageQueue(bool draw)` — per-render-frame
  dispatcher. Postfixed to compute the smoothed camera and re-pose
  entities.
- `MyRender11.SetupCameraMatrices(MyRenderMessageSetCameraViewMatrix)`
  — applies a camera message to render-thread state. Resolved via
  reflection because `MyRender11` is `internal`.
- `MyRenderProxy.UpdateRenderObject(uint, MatrixD?, …)` — public
  matrix update; **queued**, lands on the next frame's drain. Loses
  ordering to the sim thread's per-tick message; not used for
  synchronous overrides.
- `MyManagers.PostponedUpdate.SavePostponedUpdate(MyRenderMessageUpdateRenderObject)`
  — internal; stages a matrix in the manager's update dictionary.
  Resolved via reflection.
- `MyManagers.PostponedUpdate.ApplyPostponedUpdate(uint actorID)` —
  internal; flushes a single staged matrix to its `MyActor` (via
  `MyActor.SetTransforms`) immediately. Combined with
  `SavePostponedUpdate`, this is the synchronous bypass that overrides
  a matrix after the queue has drained.
- `MyManagers.PostponedUpdate.Apply()` — the engine's normal flush;
  runs at the end of `ProcessMessageQueue` for every staged matrix.
- `MyActor.UpdateWorldMatrix` / `MyActor.SetWorldMatrixDirty` /
  `MyInstance.UpdateWorldMatrix` — the parent-following machinery.
  When an actor has a non-null `Parent`, `SetWorldMatrixDirty` marks
  it dirty on every parent matrix change, and per visible instance
  per frame `MyInstance.UpdateWorldMatrix` recomputes
  `child.LastWorldMatrix = m_relativeTransform * Parent.LastWorldMatrix`.
  This runs **after** `PostponedUpdate.Apply`, so a write to a
  parented actor is silently overwritten before draw — the reason
  we redirect character writes to the cull parent.
- `MyScene11.SetActorParent` — render-thread handler for
  `MyRenderMessageSetParentCullObject`; resolves the sim-side
  `SetParent(child, parent, relative)` call.
- `MyIDTracker<MyActor>.FindByID(uint)` — public static lookup that
  returns the renderer-side `MyActor` for a given render-object id
  (the same lookup `MyRender11.ProcessMessageQueue` uses internally
  to dispatch every per-id message). Used by
  `ApplyAttachedLightSmoothing` to locate the actor of an
  unparented light without going through the queue, and by
  `BillboardSmoothing.IsEffectivelyUnparented` to resolve a
  billboard's `ParentID` the same way `MyBillboardRenderer.Gather`
  does (below).
- `MyBillboardRenderer.Gather` — a Custom billboard's corners are
  transformed by its parent actor's `WorldMatrix` **only when**
  `myBillboard.ParentID != uint.MaxValue` **and**
  `MyIDTracker<MyActor>.FindByID(ParentID) != null`. So
  `uint.MaxValue` (`MyRenderProxy.RENDER_ID_UNASSIGNED`) is *not* the
  only "render verbatim in world space" case — a non-`uint.MaxValue`
  id that doesn't resolve to a live actor renders verbatim too.
  Bulk HUD emitters lean on this: WeaponCore's combat UI passes
  `parentID 0` to `AddTriangleBillboard`, `0` never resolves, and the
  glyphs render at world coords exactly like `uint.MaxValue`. Our
  eligibility filter must mirror this (`ParentID != uint.MaxValue` by
  itself does **not** mean "parented") or such content is excluded
  from smoothing and slides through the smoothed view.
- `MyActor.SetMatrix(ref MatrixD)` — public; writes the actor's
  world matrix and dirties the cull tree. For an unparented light
  this is exactly what `MyLightComponent.UpdateData` ends up doing
  (`Owner.SetMatrix(CreateWorld(pos, dir, up))`), so we call it
  directly to bypass `UpdateData`'s per-call
  `MyManagers.Textures.GetPermanentTexture(ReflectorTexture)`
  re-acquire — pose-only updates don't need to thrash the texture
  cache.

### Cross-thread

- `MyTransparentGeometry.Camera` — alias of
  `MySector.MainCamera.WorldMatrix`. Read on the sim thread by
  billboard emission for face-camera quad orientation.
- `MyRenderProxy.AfterUpdate(MyTimeSpan?, bool)` — sim-thread call at
  the very end of each sim tick that forwards to
  `MySharedData.AfterUpdate` (the only per-tick site that calls
  `m_inputBillboards.CommitWrite()`, the swap queue's commit). We
  postfix it (`PatchAfterUpdate` in `CameraCapture.cs`) to publish
  the deferred camera/entity snapshot — pairing snapshot T with
  billboards T atomically on the render thread.

### Billboard pipeline

Separate from the `MyRenderMessage` queue. Sim-side emission and
render-side draw are decoupled by a `MySwapQueue` (sim's
`CommitWrite()` → render's `RefreshRead()`). Our
`ProcessMessageQueue` postfix is *not* a natural insertion point —
the billboard list isn't synchronized with that queue.

`MyBillboard` (in `VRageRender`) stores its data in
`Position0..Position3` (`Vector3D`) plus a `LocalType` enum that
controls *how* those slots are interpreted:

- `LocalType.Line` and `LocalType.Point` — slots hold deferred
  payload (origin, direction/length/thickness for `Line`; origin,
  radius/angle for `Point`). The actual quad is computed at
  render-frame rate inside `MyBillboardRenderer.GatherInternal`
  using `MyRender11.Environment.Matrices.CameraPosition` — which is
  already our smoothed value. **These smooth without intervention
  *when the stored origin is a true world coord*** — only the quad's
  facing depends on the camera, and that's the smoothed one. The
  exception is a depth-pulled `Point`: Build Info's `/bi cn`
  conveyor dots store `Position0 = vanillaCam + (port − vanillaCam) ×
  0.01` (a camera-relative center baked at sim rate), so the engine
  re-faces the quad each frame but renders it around a frozen center
  — the dot ghosts. `BillboardSmoothing.TryRebakePointDepthPulled`
  re-anchors it, gated on the center sitting within the
  camera-anchored threshold. Note a `Point` can't take the
  camera-relative transform a HUD `Custom` quad does (`smoothedCam +
  R_delta · (center − vanillaCam)`): the engine renders a `Point`
  *relative to the smoothed camera*, so `R_smoothed` cancels in the
  projection and the dot's screen position collapses to
  `inv(R_vanilla) · (center − vanillaCam)` — sim-rate-only, a stutter
  (the first cut shipped this). The fix recovers the true port and
  re-depth-pulls against the smoothed camera, which collapses to a
  pure translation `Position0 = posClose + (smoothedCam − vanillaCam)
  × (1 − 0.01)`; the renderer's smoothed-camera projection then
  supplies rotation tracking, giving frame-exact motion with no lerp.
  The conveyor *lines/arrows/boxes* (and their shadows) are `Custom` and
  were first left on the ordinal-lerp path, but that's the *HUD* smoother
  — it assumes a stable emission order and sparse screen positions, and
  the conveyor content violates both (Build Info re-sorts it ~once/sec and
  frustum-culls per element, and it sits ~2.5 cm apart near the camera
  inside the 0.5 m sanity gate), so a reshuffle mis-lerps adjacent lines
  or trips the tree-restructure gate — the occasional line *flicker*,
  visible even with the camera dead still (the mis-pair is about which
  line sits at an ordinal, not camera motion). They're now detected by
  **material** (`DepthPulledOverlay.IsDepthPulledOverlayQuad`): Build Info
  tags every conveyor quad with a `BuildInfo_Square` /
  `BuildInfo_ShadowedLine` / `BuildInfo_Arrow` / `BuildInfo_ShadowedArrow`
  string id no HUD framework or vanilla surface uses, so the match is
  exact. *Geometry can't do this* — a diagnostic showed short conveyor
  segments are near-square (aspect ~1.2) and indistinguishable from HUD
  glyphs, so a 2:1 aspect gate dropped ~70% of real lines; the material
  key catches long and short alike (verified `detectedConveyor ==
  eligibleCustom`). Matches route to the same frame-exact depth-pull
  recovery as the dots (`ApplyDepthPulledOverlayRecovery` — translate all
  four corners by `(smoothedCam − vanillaCam) × 0.99`; the baked
  camera-facing perp is left as-is, negligible for a sub-mm-thick line
  ~3 m out). Detected quads are excluded from the ordinal population
  (capture and rebake loops apply the *same* classifier so
  `captureIdx`/`rebakeIdx` stay aligned), which also stops their
  cull/re-sort count churn from perturbing the HUD lerp. Material ids
  resolve via the global string table, so if Build Info renames them
  nothing matches and the content degrades back to the ordinal-lerp
  rather than crashing. The material list is the one piece of
  mod-specific knowledge in the plugin; Build Info's lives in its
  `BuildInfoOverlay` profile and the engine reaches it only through the
  general `DepthPulledOverlay` recognizer — see the **Mod-specific
  recognition** section. `BillboardSmoothing` itself stays mod-agnostic.
- `LocalType.Custom` — slots hold fully baked four-corner world
  positions, computed at sim emission time using
  `MyTransparentGeometry.Camera` (un-smoothed). Covers
  `MyTransparentGeometry.AddPointBillboard`, `AddLineBillboard`,
  `AddBillboardOriented*`, `AddTriangleBillboard`, and the particle
  quads that funnel through these. **These are the billboards that
  visibly ghost.**

Render-thread entry point for re-baking: `MyBillboardRenderer.Gather`
is called once per render frame from `MyTransparentRendering`, and
runs *after* the camera matrices are set up. `GatherInternal` is
where each `MyBillboard` is turned into vertex/index data — a prefix
on `Gather` lets us mutate `Position0..Position3` of `Custom`-typed
billboards in place before that runs.

Sim-side emission sites that bake `Custom` quads (need orientation
inputs captured for re-baking):

- `MyTransparentGeometry.AddPointBillboard(... origin, radius,
  angle, ...)` — quad faces the camera at the moment of emission via
  `MyUtils.GetBillboardQuadAdvancedRotated`.
- `MyTransparentGeometry.AddLineBillboard(... origin, dir, length,
  thickness, ...)` — polyline quad oriented relative to the camera
  position via `MyUtils.GetPolyLineQuad`.
- `MyTransparentGeometry.AddBillboardOriented(... origin, leftVec,
  upVec, ...)` — caller passes the orientation axes; commonly
  callers compute them from `MyTransparentGeometry.Camera`. Whether
  these need re-baking depends on the caller — some use fixed world
  axes (e.g. signs), others use camera-derived axes.
- `MyTransparentGeometry.AddTriangleBillboard(p0, p1, p2, ...)` —
  mesh-defined verts, not a face-camera quad — should be skipped.

### HUD-framework pool rotation

Rich HUD Master (BV3, Build Vision 3.0) and Text HUD API (HUD
Compass) emit Custom billboards through `MyTransparentGeometry.
AddBillboards(IEnumerable, false)` — bulk emission, non-persistent.
Crucially, both rotate through pre-allocated billboard pools each
sim tick: RHM through 6 pools (`BillboardSwapPool(6)` in its
`Master/UI/HUD/Rendering/BillboardUtils.cs`), Text HUD API through 4
(`POOLSIZE = 4` in its `HUDApi/VersionHelper.cs`). Each pool gets
exactly one tick of emission, then `RotatePool` advances to the
next; a given `MyBillboard` reference appears in
`MyRenderProxy.BillboardsRead` only every Nth tick.

Consequences for re-baking:

- **Per-`MyBillboard`-ref caching breaks down.** Cache miss on a
  ref happens every N sim ticks, not every 1, so any prev/curr
  scheme keyed by ref lerps over an N-tick gap and produces
  N-tick-of-motion-in-one-inter-tick output (= ghosting). Confirmed
  experimentally — first lerp attempt keyed by ref produced "all
  billboards ghosting" report from the user.
- **Per-ordinal caching works.** `BillboardsRead` order = emission
  order (the swap queue's `CommitWrite` preserves
  `BillboardsWrite.Add`/`AddRange` order without reordering), and
  the i-th `AddBillboards` call across consecutive ticks emits the
  same logical UI element when the tree is stable. Cache positions
  by their index in `BillboardsRead`, not by `MyBillboard` ref.
- **Position writes happen AFTER `AddBillboards`.** RHM's pool
  billboards get added to `BillboardsWrite` first (with stale
  positions from their previous use), then RHM's
  `BillBoardUtils.FinishDraw` → `UpdateBillboards` writes the
  actual `Position0..2` later in the same sim tick (parallel-for
  batch). Eager position capture in our `AddBillboard` postfix
  would grab the stale values; lazy capture at `Gather`-prefix
  time gets the correct ones.
- **Emission position is `(P_world - camera_vanilla) ·
  inv(R_vanilla)` away from camera-local L.** RHM populates
  positions via `bb.Position0 = matrix.Translation + (planePos.X
  * matrix.Right) + (planePos.Y * matrix.Up)` where matrix =
  `PixelToWorld * Camera.WorldMatrix` for screen-space HUD; that's
  exactly `camera_vanilla + R_vanilla · L`. Recovering L on the
  render thread enables the same `smoothed_cam + L · R_smoothed`
  formula across the lerp and fallback paths, so they don't
  diverge under fast rotation (which is what world-position lerp
  did — see "Considered and rejected").

**WeaponCore is a different kind of bulk emitter** and needs
different handling (issue #1: "HUD text ghosting"). It re-lays its
*entire* combat HUD draw list from pooled objects every sim tick
(`Hud.DrawTextures`/`DrawText`), emitting each text glyph and UV
texture as two `MyTransparentGeometry.AddTriangleBillboard` calls —
and passing **`parentID 0`**, not `uint.MaxValue`. Two consequences:

- **It was excluded from smoothing entirely**, not mis-lerped. The
  `ParentID != uint.MaxValue` eligibility check skipped it, so it
  rendered at its vanilla sim-tick world coords through our smoothed
  view and slid every frame — radially about screen center under
  rotation (the smoothed camera is rotated slightly less than
  vanilla, pivoting each HUD point about the forward axis by an
  amount ∝ its distance from center — the reporter's "letters
  animate into position from the center"). This is the
  "leave-vanilla-alone → slide" artifact from "Considered and
  rejected", leaking in through the `ParentID` gate. The fix is
  `IsEffectivelyUnparented` resolving `parentID 0` as world-space
  (see the `MyBillboardRenderer.Gather` engine note above).
- **It can't use the per-ordinal lerp.** Per-ordinal caching needs a
  stable emission order; WeaponCore rebuilds its glyph stream from
  content each tick (weapon status strings, target re-sorts, aging
  distance labels, reload-icon frames), so ordinal i rarely holds the
  same logical glyph two ticks running. And the two guards that catch
  ordinal shifts elsewhere both fail here: every glyph of a font
  shares **one** atlas material (`EnglishFontMono`/`EnglishFontShadow`,
  differing only by UV), so the material fingerprint can't tell glyphs
  apart; and the whole HUD spans only a few cm in camera-local L at
  the ~0.1 m near plane, far inside the 0.5 m `OrdinalLerpMaxDistSq`
  gate. So dangling-parent content is routed to the camera-anchored
  `L_curr` reprojection (`TryRebakeCameraAnchoredFallback`) instead —
  frame-exact, order-independent, screen-locked. It takes no ordinal
  slot, so it can't perturb the RHM/Text-HUD-API lerp either.

WeaponCore's *non-text* HUD (reticle, icons via `AddBillboardOriented`)
is unaffected: that path runs through `CreateBillboard`, which sets
`ParentID = uint.MaxValue`, so it was already smoothed by the
Direct-orient path (`RebakeDirect`). Only the `AddTriangleBillboard`
(`parentID 0`) text and UV quads needed the fix.

### Light pipeline

Lights live on dedicated `MyActor`s with a `MyLightComponent`,
addressed by their own render-object id (distinct from any host
entity's mesh ids). Sim emission goes through
`MyRenderProxy.UpdateRenderLight(ref UpdateRenderLightData)` —
**not** the `UpdateRenderObject` queue. Render-side dispatch lands
at `MyLightComponent.UpdateData`, which writes
`Owner.SetMatrix(CreateWorld(pos, dir, up))` for spots /
`CreateTranslation(pos)` for points.

Most lights ride along smoothed entities for free:

- **Block lights** (`MyLightingLogic.Lights`, beacons) parent to
  the per-cell `MyCubeGridRenderCell.ParentCullObject`, which is
  registered into the grid's `RenderObjectIDs[]` array via
  `AddRenderObjectId(registerForPositionUpdates: true, ...)`. Our
  per-grid `PostponedUpdate.Apply` overrides every id in that
  array, so the parent-following machinery
  (`MyInstance.UpdateWorldMatrix`) cascades the smoothed pose down
  to the lights automatically.
- **Jetpack thrust lights** (`MyRenderComponentCharacter.
  m_jetpackThrusts[].Light`) parent to the character's
  `m_cullRenderId` — same indirection that smooths the body.

The unparented exceptions need manual smoothing —
`AttachedLightCapture` snapshots them per sim tick alongside the
host entity's pose. Currently:

- **Character headlamp** (`MyRenderComponentCharacter.m_light`).
- **Welder/grinder tool effect light**
  (`MyEngineerToolBase.m_toolEffectLight`).

`RenderFrameSmoothing.ApplyAttachedLightSmoothing` lerps position
+ slerps spot orientation per render frame and writes the smoothed
matrix straight to the light's `MyActor.SetMatrix`, bypassing both
the `UpdateRenderLight` queue (no `PostponedUpdate` equivalent for
lights) and `MyLightComponent.UpdateData` (which would re-acquire
the reflector texture each frame for a pose-only update).

`AttachedLightCapture` filters out any light whose `ParentID !=
uint.MaxValue` — those auto-follow their parent, and a world-space
override would be silently overwritten by
`MyInstance.UpdateWorldMatrix` next frame.

### Negative findings (paths ruled out)

- The renderer does **not** interpolate entity matrices between sim
  ticks. `MyManagers.PostponedUpdate.Apply` calls
  `MyActor.SetTransforms` with the matrix verbatim — no time blend.
- `MyRenderMessageUpdateRenderObject.LastMomentUpdateIndex` is never
  read in `VRage.Render11.dll`; dead at the renderer level (sim sets
  it, nothing consumes it).
- `MySkinningComponent` only updates the character's own bones; it
  does not touch attached entities' `MyActor.WorldMatrix`. Held
  weapons / attached items are not bone-driven on the render thread,
  so a synchronous matrix write is sufficient.
- `MyEnvironmentMatrices.LastUpdateWasSmooth` only feeds
  `MyRenderVoxelActor.Clipmap.Update` (voxel clipmap LOD); does not
  affect entity rendering.
- Tool subparts (drill spike, grinder wheel) are **not** reparented
  on the renderer side. `MyActor.SetWorldMatrixDirty` is a no-op
  when `Parent == null`, and the only path that sets a renderer-side
  parent (`MyScene11.SetActorParent` via
  `MyRenderMessageSetParentCullObject`) is used only for cube-block
  subparts (`MyParentedSubpartRenderComponent`). Hand-tool subparts
  use plain `MyRenderComponent`. So a synchronous matrix override on
  the subpart's actor sticks — the renderer doesn't recompute its
  world from parent×local each frame.
- `MyBillboard.LocalType.Line` and `MyBillboard.LocalType.Point` —
  not baked at sim, the quad is generated on the render thread from
  `MyRender11.Environment.Matrices.CameraPosition` (already our
  smoothed value). These smooth correctly without plugin work *as
  long as the stored origin is a true world coord*. Caveat: a `Point`
  whose center is depth-pulled toward the camera (Build Info `/bi cn`
  conveyor dots) has a camera-relative center baked at sim rate, so it
  ghosts and is re-anchored by `TryRebakePointDepthPulled` — see the
  billboard-pipeline section above.

## Considered and rejected

Approaches we evaluated but didn't ship, with the reason.

- **Camera-relative transform for depth-pulled `Point` billboards**
  (Build Info `/bi cn` conveyor dots). First cut at de-ghosting the
  dots reused the HUD-quad transform: `Position0 = smoothedCam +
  R_delta · (center − vanillaCam)`.
  - Tested. Killed the ghost (multiple copies) but left a per-sim-tick
    stutter. Reverted to depth-pull recovery (pure translation).
  - Why it stuttered: unlike a `Custom` quad, the engine renders a
    `Point` *relative to the smoothed camera*, so the projection is
    `inv(R_smoothed) · (Position0 − smoothedCam)`. Substituting the
    transform, `R_smoothed` cancels and the screen position reduces to
    `inv(R_vanilla) · (center − vanillaCam)` — a value built only from
    sim-tick quantities, so it's frozen within a tick and jumps at the
    boundary. The depth-pull-recovery formula (`posClose + (smoothedCam
    − vanillaCam) × 0.99`) instead leaves rotation to the renderer's
    own smoothed projection, which doesn't cancel. See
    `TryRebakePointDepthPulled`.
- **Geometric classifier for depth-pulled conveyor *line* quads**
  (`/bi cn` lines flickering on Build Info's re-sort). To route the
  bulk-emitted conveyor lines off the HUD ordinal-lerp and onto
  depth-pull recovery, the first cut detected them by shape: every
  corner near the camera + a thin, elongated rectangle (short edge
  < 2 cm, long edge ≥ 2× short).
  - Tested. No change — the flicker persisted. Diagnostic logging
    (`eligibleCustom` / `detectedLines` / per-gate failure counts)
    showed the aspect gate rejecting ~70% of real conveyor lines:
    `failAspect` dominated, with a sample short segment at 2.5 mm ×
    4.5 mm (1.8:1). Switched to material-keying and the flicker went.
  - Why geometry can't work: conveyor connections come in all lengths,
    and a *short* segment is near-square after the ×0.01 depth-pull —
    geometrically identical to a HUD text glyph or icon. No aspect or
    size threshold separates "short conveyor line" from "HUD quad."
    The reliable signal is the **material**: Build Info tags its
    conveyor quads with `BuildInfo_*` string ids nothing else emits, so
    `DepthPulledOverlay.IsDepthPulledOverlayQuad` matches on those (plus a
    near-camera sanity gate) and catches long and short alike.
  - Lesson (again): when a candidate fix doesn't move the symptom,
    instrument before iterating. The geometric gate *looked* right on
    paper for the lines I pictured (1-block spans); the log showed the
    real population was dominated by short segments it silently dropped.
- **Span-based gate for depth-pulled overlay *lines***
  (`CameraPulledSpanSq`, the orient-path analogue of the conveyor
  material key). `RebakeLine` first classified a line as depth-pulled
  (see-through, needs recovery) vs solid (world-anchored, leave alone)
  by world-space span: the ×0.01 depth-pull shrinks a line to a tiny
  span, so `span² < 0.1²` meant "depth-pulled." Span was chosen over
  distance-from-camera precisely because a solid line can be near the
  camera (a cockpit edge you're sitting in).
  - Worked for Build Info's short see-through-walls hairlines and the
    short conveyor segments, but failed for the Measure tool's long
    see-through measurement lines: a see-through line is `0.01 × the
    measurement length`, so a 10 m+ measurement's copy exceeds the span
    gate and was misclassified as solid → left frozen at the
    depth-pulled pose (stutter), with a frame-to-frame flip at the
    boundary (flicker).
  - Switched to **thickness + near-camera**: the depth-pull also scales
    *thickness* ×0.01 (≤~1 mm) regardless of the feature's length, while
    solid lines keep full thickness (≥~cm) even when near — so it keeps
    the cockpit-edge property span had, without span's length
    dependence. The same near-camera + tiny-*radius* signal handles
    depth-pulled point markers in `RebakeAxisAligned` (Measure vertices),
    so points and lines now share one discriminator.
  - Lesson: a geometric gate keyed on a quantity the depth-pull scales
    uniformly with the content (span ∝ length) breaks when the content's
    real size varies; key on a quantity the depth-pull fixes (thickness/
    radius are ×0.01 of a roughly-constant base) instead.
- **Forward camera extrapolation instead of 1-tick-delayed
  interpolation.** Compute the camera as
  `Lerp(prev, curr, 1 + alpha)` so it predicts the next sim tick
  rather than reconstructing the previous one. Rationale at the
  time: the renderer applies entity matrices from the *latest* sim
  tick with no engine-side interpolation, so a 1-tick-delayed
  camera drives a 1-tick mismatch against the entities.
  - Tested. Looked subjectively worse — direction changes amplified
    visible wobble — and the original tool/character ghosting was
    still present. Reverted.
  - Why it didn't fix the underlying bug: the ghosting wasn't
    camera *lag*, it was that the affected entities are positioned
    by the sim thread relative to the vanilla camera. Projecting
    them with any smoothed camera — interpolated or extrapolated —
    reveals the same per-tick offset proportional to the smoothing.
    The actual fix was to mirror the smoothed pose onto the affected
    entities, which made the camera's interpolation strategy a
    non-issue.
- **Install camera matrices at `DrawScene.Prefix` and restore
  vanilla in `DrawScene.Postfix`** instead of installing in the
  `ProcessMessageQueue` postfix and leaving them live. v0 (the
  prior plugin generation) used this pattern; the rationale was
  that keeping every pre-DrawScene phase (`UpdateGameScene`,
  `ProcessUpdates`, `m_inputBillboards.CommitWrite`) on the vanilla
  camera, plus restoring vanilla after the frame so the next
  sim-tick delta isn't poisoned, might eliminate emergent wobble
  from the smoothed camera bleeding into upstream phases.
  - Tested while chasing the depth-pulled-line wobble. No visible
    change; the wobble persisted identically. Reverted.
  - Why it didn't help: the wobble's actual cause was the
    sim-emission depth-pull mechanism on Build Info's overlay lines,
    not the install timing.
- **Per-`MyBillboard`-ref prev/curr lerp for the BV3/HUD-Compass
  stutter.** First swing at smoothing pool-rotated HUD content:
  cache `(prev, curr) Position0..3` per `MyBillboard` reference,
  lerp at the camera's smoothing alpha each render frame.
  - Tested. All billboards immediately ghosted. RHM rotates
    through 6 pre-allocated pools and Text HUD API through 4, so
    a given ref reappears in `BillboardsRead` only every 4–6
    ticks. The cache-miss block then shifted curr→prev across an
    N-tick gap, so the lerp was interpolating over 4–6 ticks of
    motion in a single inter-tick. Reverted.
  - Switched to **per-ordinal** caching (index in `BillboardsRead`
    = emission order, preserved across pool rotations as long as
    the UI tree is stable). Logical-element identity tracks
    correctly through the rotation.
- **Greedy fingerprint+window matcher** for picking each curr
  ordinal's prev when third-party mods (Build Info) shift
  ordinals mid-list. Match by `(material, blend, parentID,
  customViewProjection, isTriangle)` within a ±16-ordinal window,
  break ties by position proximity, claim each prev once.
  - Tested. Reported "chaos." Greedy claims cascade — one
    incorrect early claim forces every later same-fingerprint
    curr to find a sub-optimal prev, which in turn forces every
    later one, and so on. With BV3's 50-100 panels sharing the
    same triangle/PostPP/uint.MaxValue fingerprint, a single
    mis-claim avalanches across the menu. Reverted to a simple
    per-ordinal sanity check (no cross-ordinal coordination).
- **World-position lerp with camera-relative-transform fallback.**
  Per-ordinal sanity check; on pass, `lerp(P_prev, P_curr, α)`;
  on fail, `smoothed_cam + (P_curr - vanilla_cam) · deltaRot`.
  - Tested. Worked for slow camera motion but flickered under
    fast view rotation. Linear lerp of two world points produces
    a chord whose midpoint is closer to the camera than the
    slerp-equivalent (the rotation arc); the fallback uses slerp
    via the deltaRot quaternion. So when the sanity check
    alternated per tick (Build Info shift), even the same static
    panel got a slightly different screen position from each
    path — visible flicker proportional to per-tick rotation
    speed.
  - Switched to **camera-local L lerp**: cache `L = (P_world -
    camera_vanilla) · inv(R_vanilla)` instead of P, and apply
    `smoothed_cam + L · R(smoothed)` for both lerp and fallback
    paths. For static screen offsets `L_prev == L_curr`, the
    lerp output equals the fallback output exactly — the
    chord-vs-arc divergence disappears, no per-tick alternation
    reads as flicker.
- **Plain "leave vanilla position alone" fallback** when the
  per-ordinal sanity check fails. Intent: if the lerp can't fire,
  let the engine render the billboard at its vanilla world
  position through the smoothed view — that's the original
  pre-plugin sim-rate behavior, which the user explicitly
  preferred over flicker.
  - Tested. Still read as flicker, not stutter. Through a
    smoothed view, vanilla curr positions slide by camera
    per-tick velocity within each inter-tick (camera moves, panel
    doesn't). When that alternates per tick with the lerp's
    smooth motion, the alternation is between two distinct
    motions, not between motion and stillness — the eye picks
    out the change in motion direction as flicker. Reverted in
    favor of the L-lerp approach, where both paths share the
    `smoothed_cam + L · R(smoothed)` formula and the
    alternation is invisible on static content.
- **Cache keyed by raw `BillboardsRead` index.** First L-lerp
  iteration counted ordinals over every entry in
  `BillboardsRead`. Build Info's line/point billboards (which
  take orient-based rebakes, not the lerp) still occupied
  ordinal slots; when Build Info's per-tick count fluctuated as
  the user rotated the view (highlighted blocks changing), every
  HUD billboard's raw ordinal shifted by the same amount, and
  the per-ordinal sanity check failed on each shift. The fix:
  compacted ordinal — count only over lerp-eligible billboards
  (Custom + default ParentID/CVP + no orient), so
  orient-registered billboards don't shift the HUD's cache index.
  With this, third-party mods emitting orient-registered content
  (Build Info, particle systems, etc.) don't perturb the HUD's
  lerp at all.
- **A zero-knowledge signal for depth-pulled overlay Custom quads**
  (conveyor lines/arrows/boxes, LIDAR cloud), to drop the material key
  entirely so *any* overlay mod works without recognition. Every
  candidate general signal was evaluated and rejected:
  - *Blend type.* The shared technique is `BlendTypeEnum.PostPP` (the
    engine's after-post-processing pass, `MyBillboardRenderer.RenderPostPP`).
    But Rich HUD Master and Text HUD API draw essentially all their HUD
    with PostPP too, so it can't separate a world overlay from screen HUD.
  - *Material property.* The overlay materials are private textures whose
    `TransparentMaterials.sbc` definitions carry no "ignore-depth /
    always-on-top" flag — the see-through effect is purely the depth-pull
    geometry + PostPP, so there's no material *property* to key on, only
    the name.
  - *Geometry.* Already rejected twice (aspect-ratio gate, span gate) —
    short segments are square, and arrows/boxes/LIDAR-pixels are square,
    indistinguishable from HUD glyphs.
  - *Cross-tick motion.* A depth-pulled overlay sits so close to the
    camera that across a sim tick it translates by ≈ the camera delta —
    the same as camera-locked HUD (they differ only by the depth-pull
    ratio, ~1%). They diverge only under rotation, exactly where the
    1-tick-delayed camera makes per-element tracking unreliable.
  - Conclusion: for the bulk-emitted Custom quads the material is the only
    reliable per-element signal, so it stays — but the *coupling* is
    generalized: the engine keys on the general `DepthPulledOverlay`
    recognizer and Build Info's materials are one profile (a new mod adds
    another). The dots and orient-carrying lines need no material
    (near-camera `Point`s and hairline lines are rare, so geometry is clean
    there); only the Custom quads, dominated by HUD, resist.
- **Inverting the recognition — recognize HUD/known content, depth-pull
  the unrecognized remainder.** Tempting because it would make *any*
  overlay mod's quads work (they'd fall in the remainder). Rejected as a
  strictly worse trade:
  - The HUD path is *already* the generic mechanism — the per-ordinal
    lerp smooths any HUD mod without recognizing it (Build Vision via RHM,
    HUD Compass via Text HUD API, both work with zero mod-specific code).
    Inverting sacrifices the one path that already generalizes to chase
    the one that can't.
  - The "HUD to exclude" set is *harder* to recognize than the overlays:
    RHM/Text HUD API content has the identical billboard signature to
    Build Info's overlays (bulk `Custom`, PostPP, near-camera, default
    parent/CVP), so you'd have to couple to the frameworks' *emission*
    internals — more mods, more fragile than a stable `BuildInfo_Square`
    string id — and still break any HUD mod that bulk-emits Custom/PostPP
    quads directly through the vanilla API.
  - The failure mode flips from graceful to harsh. Today an *unrecognized
    overlay* degrades to the HUD lerp (ghost / occasional flicker — a
    transparent-overlay nuisance). Under the inversion an *unrecognized
    HUD element* gets the translation-only recovery, which doesn't
    re-anchor to camera rotation, so it slides across the screen under any
    view rotation — the primary UI visibly wobbling. And there's no single
    neutral recovery for both: the depth-pull translation and the
    camera-relative transform agree under translation and diverge under
    rotation precisely because one tracks the world and the other the
    camera.
