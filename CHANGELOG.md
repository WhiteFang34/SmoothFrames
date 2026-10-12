# Changelog

## v1.1.0 — 2026-10-12

- Optimized smoothing around large builds. Grids at rest no
  longer cost render time every frame.
- Optimized the per-tick capture of nearby grids, which no
  longer visits every block on every tick.
- Optimized the interpolation toggle. Turning it off now
  stops the capture work as well.

## v1.0.4 — 2026-10-07

- Fixed spinning wheels wobbling, most visibly on 3x3 and
  5x5 wheels at speed.

## v1.0.3 — 2026-06-06

- Fixed Build Info conveyor overlay (lines, arrows, and dots)
  ghosting and flickering.
- Fixed WeaponCore combat HUD text drifting across the screen
  when rotating the view.

## v1.0.2 — 2026-05-27

- Fixed block-placement preview ghosting when placing a small grid
  block on a large grid.

## v1.0.1 — 2026-05-17

- Fixed Rich HUD Master menus (e.g. Build Vision 3.0) showing
  scrambled content.

## v1.0.0 — 2026-05-15

- Initial release.
