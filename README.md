# Smooth Frames

A Space Engineers plugin that smooths motion to your monitor's
refresh rate. Vanilla SE updates the world 60 times per second, so
even on a 144 Hz or 240 Hz monitor, motion looks like 60 FPS — each
render frame is a copy of the last sim tick until the next one lands.
Smooth Frames interpolates the camera and nearby entities between
sim ticks, so every render frame shows a distinct moment of motion.
The engine's 120 FPS cap is also lifted, for users above that.

## Install

1. Install [Pulsar](https://github.com/SpaceGT/Pulsar) (link to
   installer in its README).
2. Enable **Smooth Frames** in the **Plugins** dialog.
3. Apply, restart the game.

## Controls

- **Ctrl+F11** — toggle interpolation on/off. Hot-togglable; the next
  render frame falls through to vanilla rendering when off, no
  transition needed.

## Configuration

Open Pulsar's plugin list and click the settings cog next to **Smooth
Frames** to bring up a dialog with:

- **Interpolation enabled** — checkbox, mirror of the Ctrl+F11 hotkey.
  Both sources persist to the same setting.
- **Frame rate cap** — slider, 60 Hz to your monitor's refresh rate.
  Rewrites the target frequency on the engine's render-thread
  frame-rate waiter so the renderer runs up to this many frames per
  second when the GPU has headroom. Defaults to your monitor's refresh.

Changes apply immediately and persist to
`%APPDATA%\SpaceEngineers\SmoothFrames.cfg`.

## Compatibility

Smooth Frames is a client-side rendering plugin only — it doesn't
touch network state, so it works on any multiplayer server without
the server having it installed, and can't cause desync.

It targets engine surfaces, not specific mods or plugins. Designed
to work with mods built on **Rich HUD Master** and **Text HUD API**,
and with plugins that hook into the billboard and camera systems.

## How it works

Vanilla SE drives the camera at sim rate (60 Hz) and the renderer
applies entity matrices from the latest sim tick verbatim — between
ticks, the world is frozen. The plugin runs two patches around that
loop:

- **Sim thread** — a postfix on `MyCamera.UploadViewMatrixToRender`
  snapshots the camera and the entities that need to stay in
  lockstep with it (held tool, piloted grid, local character, their
  subparts/children). The snapshot is published at the end of the
  tick, after the billboard swap-queue commit, so the render thread
  always sees a matched (camera, billboards) pair.
- **Render thread** — a postfix on `MyRender11.ProcessMessageQueue`
  computes `alpha = (now − currTick) / dt`, re-poses the camera at
  `lerp(prev, curr, alpha)` via `MyRender11.SetupCameraMatrices`,
  and re-poses each captured entity at `lerp(prev_pose, curr_pose,
  alpha)` through a synchronous `PostponedUpdate` bypass (the
  normal queued path lands one frame late and loses ordering to the
  sim thread's next per-tick message).

Around the camera/entity smoothing, a handful of subsystem patches
keep the rest of the world in step: custom billboards (Build Info
hairlines, depth-pulled lines), GPU particle effects (welder flame,
tool sparks, drill dust, muzzle flash, and similar), unparented
lights tied to a smoothed entity (character headlamp, welder/grinder
contact glow), HUD markers (GPS labels, off-screen arrows), the
block-placement preview, and the artificial horizon / crosshair.
