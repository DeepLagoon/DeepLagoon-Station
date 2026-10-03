# Ambient occlusion

Client-only contact shading; enabled by default. The default renderer is Silhouette.
RobustToolbox is unmodified. Lighting and dynamic shadows use the engine normally.
The overlay runs below entity sprites and before the final FOV pass.

## Settings

Graphics options provide the AO toggle, intensity (100–300%) and structure/mob
contact toggle. Walls remain shaded when structure/mob contacts are disabled.

- `graphics.ambient_occlusion`: true by default.
- `graphics.ambient_occlusion_intensity`: 300 by default.
- `graphics.ambient_occlusion_entities`: true by default.
- `graphics.ambient_occlusion_silhouettes`: true by default. Existing saved
  preferences take precedence; use `cvar graphics.ambient_occlusion_silhouettes true`
  to switch an existing configuration from Mask.
- `graphics.ambient_occlusion_silhouette_scale`: 118 by default; clamped to 100–140%.

## Rendering

Walls and structures reuse their current sprite frames, with an expanded black
silhouette displaced exactly four source pixels (screen vector 2.4,-3.2).
Directions, animation frames and layer transforms are read through public APIs.
The original sprite is never modified. One copy is drawn per eligible layer;
custom-shader, shader-parameter and unshaded layers are excluded.
Items, point lights and airlocks cast no silhouettes. Enabled occluders still
block visibility regardless of caster eligibility.

Mobs use one shared 16×16 nearest-filtered weak contact texture, clipped to
nonempty floor tiles and excluded from wall tiles. Silhouette allocates no large
viewport masks and performs no recurring mask uploads.

A spatial query and current-state eligibility checks run each draw. Hidden,
contained, detached, terminating and invisible entities are excluded. Current
caster LOS is checked each frame. The visibility index is reused until blocker
UIDs or exact bounds change, independent of enumeration order. Construction,
destruction and visibility changes take effect on the next draw.
At most 512 nearest visible casters are drawn. Distant contacts beyond the budget
are omitted. No global sprite scan or timer-delayed visibility cache is used.

## Diagnostics

`ao_profile 10` compares Off → Walls → Mask → Silhouette → Legacy, ten seconds
per mode plus two seconds of warm-up (60 seconds total). Settings are overridden
only during profiling and are not saved. `ao_profile stop` restores normal settings.
Legacy can reproduce severe slowdown. These alternate renderers remain available
for diagnosis; they are not the default.

Reports include frame time, p95/p99, long-frame counts, AO CPU phases, candidate
processing and visibility-index reuse. AO CPU excludes deferred renderer/GPU work.
Sequential phases may encounter different scene loads; FPS differences are not
universal guarantees.

Before feature-test cleanup, the build, sandbox check and 21 targeted tests passed.
Feature-specific tests, benchmark output and temporary validation builds were
removed at the user's request. Other project tests remain intact.
Player testing demonstrated substantially lower CPU cost than Mask/Legacy.
No direct in-game visual verification was performed by the coding agent.

## Limits

This is artistic contact shading, not physical ambient occlusion. Silhouettes
can extend slightly over neighboring surfaces, and different sprites/mob spots
can accumulate darkness where they overlap. Unsupported shader layers may omit
parts of a silhouette. Walls are identified by the Wall tag for wall-only settings.
Objects without a grid are excluded. Dense scenes beyond the caster budget omit
some shadows. Verify building/removing walls, airlocks, hidden entities, rotated
grids and dense scenes visually in a running client.
