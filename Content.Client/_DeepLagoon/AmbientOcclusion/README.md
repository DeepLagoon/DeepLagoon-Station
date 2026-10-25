# Ambient occlusion

Client-only contact shading; enabled by default. The default renderer is Silhouette.
RobustToolbox is unmodified. Lighting and dynamic shadows use the engine normally.
The overlay runs below entity sprites and before the final FOV pass. It is the only
AO overlay: the old `light.ambient_occlusion` pass has been retired so the graphics
toggle controls all contact shadows.

## Settings

Graphics options provide the AO toggle, intensity (100–300%), draw distance
(8–32 meters), caster limit (256–2048) and structure/mob contact toggle.
Walls remain shaded when structure/mob contacts are disabled.

- `graphics.ambient_occlusion`: true by default.
- `graphics.ambient_occlusion_intensity`: 300 by default.
- `graphics.ambient_occlusion_draw_distance`: 24 meters by default, clamped to 8–32.
  Distance is measured from the viewport eye to the caster in world units (one
  meter is one standard tile). Contacts fade smoothly over the outer 20% of the
  distance, up to four meters. The setting affects silhouettes and entity contacts;
  diagnostic Mask/Legacy wall tile shading still covers the viewport.
- `graphics.ambient_occlusion_max_casters`: 768 by default (previously a fixed 512),
  clamped to 256–2048. Raising it keeps more shadows in dense scenes at a CPU/GPU cost.
- `graphics.ambient_occlusion_entities`: true by default.
- `graphics.ambient_occlusion_silhouettes`: true by default. Existing saved
  preferences take precedence; use `cvar graphics.ambient_occlusion_silhouettes true`
  to switch an existing configuration from Mask.
- `graphics.ambient_occlusion_silhouette_scale`: 118 by default; clamped to 100–140%.

## Rendering

Walls and structures reuse their current sprite frames, with an expanded black
silhouette displaced exactly four source pixels (screen vector 2.4,-3.2).
Directions, animation frames and layer transforms are read through public APIs.
The original sprite is never modified. One silhouette is sampled per eligible layer;
custom-shader, shader-parameter and unshaded layers are excluded.
Items, point lights, airlocks and entities with `PuddleComponent` cast no silhouettes.
Puddles are also excluded from Mask and Legacy contacts. Enabled occluders still
block visibility regardless of caster eligibility.

Mobs use one shared 16×16 nearest-filtered weak contact texture, clipped to
nonempty floor tiles and excluded from wall tiles. All renderers share one linear
RGBA8 GPU target per viewport, cleared to black with alpha one each frame and
resized/disposed with the viewport. Silhouette performs no recurring CPU mask uploads.

Every contact uses adjacent stock Subtract/Add draws. With source and destination
alpha fixed at one, their RGB result is `max(0, previous - opacity) + opacity`,
which equals `max(previous, opacity)`. This combines sprite layers, structures,
mobs and overlapping grids without accumulating darkness or depending on draw
order. The paired draws must stay adjacent, and the target must remain linear
UNORM (not sRGB or floating-point), because subtraction relies on clamping at zero.

The resulting mask is composited once, using the current viewport light map at
each pixel. Shadow opacity is multiplied by `1 - smoothstep(0, 1, brightestChannel)`:
light fades the shadow out, full light removes it, and saturated colored lights
work too. Fullbright/disabled map lighting also removes AO. The world is darkened
only in this final pass; entity sprites and the final FOV pass retain their order.

A spatial query and current-state eligibility checks run each draw. Hidden,
contained, detached, terminating and invisible entities are excluded. Current
caster LOS is checked each frame. The visibility index is reused until blocker
UIDs or exact bounds change, independent of enumeration order. Construction,
destruction and visibility changes take effect on the next draw.
At most the configured number of nearest visible casters are drawn. The farthest
quarter of that budget fades smoothly by distance rank before being omitted. The
fade does not shift when a single distant outlier leaves the viewport.
Equal-distance candidates use a stable entity-ID tie break.
Hidden casters do not consume the Silhouette/Mask budget. No global sprite scan or
timer-delayed visibility cache is used. The draw distance cannot extend server PVS
or reveal entities outside FOV.

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

`Content.Tests/_DeepLagoon/AmbientOcclusionTests.cs` checks puddle exclusion,
non-stacking wall corners, draw-distance fading and smooth budget transitions.
`node Tools/ambient-occlusion/render.test.cjs` runs the production shader bodies
with stock blend factors in WebGL 1 / OpenGL ES 2. Set `PLAYWRIGHT_PATH` when
Playwright is installed outside the normal Node module path. The checks cover
mixed casters, transparent layers, reversed order, repeated overlap, removal and
white/colored lighting. Local results go to `.local/codex/ao-lighting/`.

Paired blending adds a draw per shadow primitive and a viewport composite; use
`ao_profile` to measure a running client after changes. Browser shader checks do
not establish native-client rendering or in-game performance.

## Limits

This is artistic contact shading, not physical ambient occlusion. Silhouettes
can extend slightly over neighboring surfaces. Unsupported shader layers may omit
parts of a silhouette. Walls are identified by the Wall tag for wall-only settings.
Objects without a grid are excluded. Dense scenes beyond the caster budget omit
some shadows. Verify building/removing walls, airlocks, hidden entities, rotated
grids and dense scenes visually in a running client.
