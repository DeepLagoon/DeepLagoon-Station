# Light bloom

Client-only soft additive bloom for enabled PointLight sources, without a MOLOT code port.
The shader uses a smooth local halo rather than a viewport-wide blur: no scene copy,
recurring texture uploads, or extra render targets. Its visibility mesh stops at the
actual convex occluder polygons, including doors and rotating grids. Light offsets,
colors, energy, directional masks and mask rotations follow PointLight settings.
Invisible, contained, detached and terminating sources are excluded.
Console bases (floor, tabletop and wall-mounted) use NoLightBloom to exclude their
decorative halos while preserving native lighting and screen shaders. Hidden sources
do not consume the nearest-light budget. Bloom is drawn above sprites, below final
FOV, world overlays and speech-bubble/interface controls; it never samples UI pixels.

Graphics options expose an enabled-by-default toggle and 0–100% intensity (default 50%).
Console settings:

- `graphics.light_bloom`: enabled by default.
- `graphics.light_bloom_intensity`: 50, clamped to 0–100.
- `graphics.light_bloom_max_lights`: 64, clamped to 16–128.

The radius is bounded by 20% of the native light radius and at most 1.5 meters.
The farthest fifth of the light budget fades. A halo with more than 128 nearby
occluder edges is omitted instead of ignoring walls. Active geometry is queried
per viewport; no delayed visibility cache is used. CPU mesh generation needs
in-game profiling on representative maps; the limits do not guarantee an FPS figure.

Lamp visibility is corrected independently of the bloom toggle. Fixture sprites
(BloomOverlayVisuals, PoweredLightVisuals or EmergencyLight) are masked when their
native light origin is behind an occluder relative to that viewport's eye. Layer
visibility flags, animations and light state stay under their normal visualizers.
Fullbright/no-FOV eyes can still see fixtures. The older SCP point/cone overlays
and their settings are retired so saved settings cannot re-enable the leaking path.

Automated checks cover occlusion, thin barriers, polygon corners and rotated grids.
Visual tuning, native client rendering and actual FPS remain manual checks.
