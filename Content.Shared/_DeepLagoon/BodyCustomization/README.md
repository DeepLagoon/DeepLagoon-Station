# Body customization

Cosmetic adaptation of MOLOT-BlueMoon-Station's `/obj/item/organ/genital` appearance.
The character editor's **Основное → Части тела** section edits the existing draft.
Presence, shape, size stage, skin/custom color and visibility are
stored in `HumanoidCharacterAppearance.BodyFeatures`, persisted by both database
providers and loaded into networked `HumanoidAppearanceComponent.BodyFeatures`.
Existing profiles default to no additional parts. Choices are independent of sex.
Breasts include the separate color toggle and nipple overlay from
https://github.com/BlueMoon-Labs/BlueMoon-Station/pull/4108, pinned to commit
`e0681c819a195c390ded3109b2109ea09b2486c1`. The source's supported states are the
ordinary pair, sizes A–O and HUGE. Both colors are saved in the same cosmetic JSON.
Hands/gloves stay above exposed anatomy; under-clothing mode restores native order.
SS13-specific underwear DMI changes are not native SS14 clothing resources.

Seven external parts have directional overlays. `butt` is the source's combined
buttocks/hips overlay, not a separately modelled hip organ. `womb` has no on-mob
sprite in the source: its presence is an additional toggle inside the vulva
settings and does not affect the sprite. The anus uses the default shape without
a shape selector. No physiology, fluids or surgery mechanics are provided. Discrete sizes come from available sprite states; individual scaling
is disabled and legacy scale values normalize to 1.

The BODY feature catalog validates shape/size against compiled resource IDs.
Client-provided paths are never loaded. Legacy scale, duplicate/unknown parts,
colors and visibility are normalized when importing/loading a profile.
Overlay layers follow the body rotation and entity scale. The front layers sit
below the jumpsuit or above outer clothing, according to the selected visibility.
Back layers remain behind the chest; a removed/hidden chest hides its anatomy.
Ordinary and luminous tattoo layers draw above anatomy, including anatomy set to
draw above clothing. Tattoo cache order is rechecked after anatomy changes.
Source overlays use the standard 32px biped torso; unusual species may require
additional art.

## Editor limits and runtime controls

The editor and server preference validation cap buttocks/hips at 4, belly at 3,
breasts (all three shapes) at J and penis at 3. The anus is a presence toggle
under buttocks/hips, fixed to size 1 and inheriting that section's color and
visibility. Testicles keep their full settings inside the penis section.
The womb remains a presence toggle under the vulva. Imported/old editor profiles
are clamped without changing the full runtime sprite catalog.
The editor preview uses a fixed render scale: padded fitted clothing layers
must never cause automatic shrinking of the character.

The third **Части тела** tab in the Ctrl+Shift+click interaction panel changes
visibility (hidden / under clothing / over clothing) for the player's own enabled
external parts, including currently hidden parts. The tab always controls the
attached character, even when the panel's interaction target is someone else.
Requests require a valid open panel and are validated by the server. Changes
are networked for other observers and last only for the current round.

Ten reagents resize existing enabled parts, including hidden ones. Every 5 units
actually metabolized changes one stage. Partial doses accumulate on the mob;
opposite doses cancel their unfinished progress for the same part. Growth and
reduction stop at the first/last available sprite for the current shape and can
exceed lobby limits. They never create a missing part or modify saved profiles.

All recipes need **370 K** and use **1 Silicon + 1 Aluminium (growth) or
1 Copper (reduction) + 1 part-specific ingredient**, producing 3 units.
The recipes have no ingredient-set subset/superset overlap with existing recipes.

| Part | Growth reagent | Reduction reagent | Third ingredient | Runtime stages |
| --- | --- | --- | --- | --- |
| Buttocks/hips | DLButtGrowth | DLButtReduction | Iodine | 0–8 |
| Belly | DLBellyGrowth | DLBellyReduction | Lithium | 1–10 |
| Breasts | DLBreastsGrowth | DLBreastsReduction | Sulfur | A–O, HUGE, MASSIVE, GIGA, IMPOSSIBLE |
| Anus | DLAnusGrowth | DLAnusReduction | Sugar | 0–8 |
| Penis | DLPenisGrowth | DLPenisReduction | Sodium | 1–5 |

Each reagent has `Pill<ReagentId>` (5u, one stage) and
`PillCanister<ReagentId>` (7 pills) entity prototypes. They are spawnable stock;
this change does not add them to vendors or roundstart loadouts.

Validation: `BodyCustomizationTests`, `BodyCustomizationEditorTests` and
`BodyCustomizationRuntimeTests` cover editor bounds, draft isolation, full-range
dose effects, heated reactions, recipe conflicts, pill contents and network
visibility authorization. `Tools/body-customization/body-editor-browser.test.cjs`
checks nested sections and compact toggle dimensions at three viewport widths,
plus the third panel tab and its visibility action.

## Soft clothing fit

`ClothingBodyFitSystem` fits equipped fabric to parts whose visibility is **under
clothing**. It works on the ordinary character sprite, including equipment in the
character preview. Hidden parts and parts explicitly exposed above clothing do
not stretch fabric. Tattoo layers retain their existing order underneath clothing.

Each direction uses the actual torso/leg and anatomy silhouettes; tails are not
part of the fitting mask. A small shape supplies each part's attachment area.
A garment must cover at least half of that area or its attaching body edges.
The edge check accommodates side-view hand cut-outs without discarding the part.
Pants remain independent of breasts. Separate visual layers of one garment use
their combined fabric silhouette so shirts, trousers and belts deform together.

The inverse warp stretches contiguous fabric from the relevant body edge and
stretches hanging parts vertically from their attachment. Sleeves outside the
expanded torso remain at their original position. It samples the original
garment's pixels, preserving cut-outs over skin. Where a hanging part covers the
gap between the native legs, it samples adjacent trouser fabric instead of
extending the leg gap into that part. Multiple parts compose in drawing order.

Native hand silhouettes provide a separate fixed mask, never extra body volume.
If a warped hand opening would reveal originally clothed skin below the hand,
the map restores the original fabric there. This prevents holes below gloves
without closing intentional cut-outs elsewhere. The hand geometry is part of
the cache key, while gloves and tails do not enlarge the fitting silhouette.

Front-facing breasts and rear-facing buttocks also give the cloth a subtle volume
hint. A softened silhouette distance field, with a shallow valley between paired
volumes, bends the original fabric pattern by at most one source pixel and adds
gentle overhead shading. Skin colors and anatomical surface details never transfer
to clothing. The effect stays inside the selected part, preserves combined fabric
coverage and avoids fixed hands. Side views retain the ordinary fit. Rear contours
start below the upper third of the minimum attachment: this keeps
the waist and lower back free of shading even when larger buttocks extend upward.
Both shading and pattern bending use this boundary. The lighting factor occupies
the inverse map's B channel (128 is neutral), so this uses the same
cached texture and shader sample, with no additional per-frame image processing.

Species/sex displacement is composed into the inverse map first. The original
RSI still controls animation and facing; layer tint, lighting and stencil rules
are retained. The padded drawing area accommodates expanded fabric without
clipping at the original frame's edge. Generated maps are shared by geometry (color changes
do not create new maps), with an idle cache limit of 128. There is no per-frame CPU
image processing or texture upload. Each fitted layer adds a non-drawing frame-UV
helper and one extra texture sample in its shader. Unequip, appearance changes
and entity removal restore the original layer and release shader instances.

`Clothing.bodyFit` accepts:

- `Auto` (default): fit uniforms, outer clothing and neck garments. Minor fabric
  protection (lab coats and winter coats) is allowed. Physical armor with more
  than 20% resistance or a flat physical damage reduction is treated as rigid;
  this is a default heuristic, overridable per item. Hardsuit and EVA bases
  explicitly use `Rigid`.
- `Soft`: also fit protective fabric, such as a particular padded armored coat.
- `Rigid`: keep the original sprite, for plates, frames and rigid accessories.

Example for a soft protective coat:

```yaml
- type: Clothing
  bodyFit: Soft
```

The general fitter supports four/eight-direction RSI layers up to 64x64, with at
most 64 direction/frame combinations and static species displacement maps. Layers
with custom shaders or unsupported formats keep their original renderer. This is
a pixel-art deformation, not a cloth physics simulation; unusually cut garments
may need a dedicated sprite or an explicit `Rigid` override.

Validation: `ClothingBodyFitTests` checks source texture/cut-out preservation,
species displacement and large anatomy against the real engineering uniform in
four directions for both sexes. A combined Vulpkanin regression uses butt size 4,
breast size J and all five mercenary medic uniform layers, checking coverage and
excess sleeve width. It also checks for skin between fabric and combat gloves,
including inside the native body silhouette, and preserves shoulder/buckle details.
Front/rear contour checks compare coverage with the plain fit, keep pixels outside
the selected part unchanged, and verify that recoloring skin cannot change fabric.
`ClothingBodyFitIntegrationTests` exercises both uniforms with gloves,
equipping, appearance refresh, exposed anatomy, rigid suits and cleanup.
`Tools/body-customization/clothing-fit-shader.test.cjs` uses Playwright/Edge WebGL
to compile the shader and verify every output pixel for four directions and two
animation frames, including neutral and shaded map values (32,768 pixels).
It complements, rather than replaces, an in-game
visual check with representative garments and species.

## Imported assets

Run from repository root with Python and Pillow:

```
python Tools/body-customization/import_bluemoon.py C:/old_disk/repos/MOLOT-BlueMoon-Station
```

The importer preserves DMI frame order (South, North, East, West), timing and
FRONT/BEHIND separation. It imports neutral tintable states and excludes duplicate
skin-tone variants. Unnamed DMI states still occupy tiles and are counted when
locating subsequent frames. The generated shared catalog contains only exported states.
Source paths and CC-BY-SA-3.0 attribution are recorded in each RSI's `meta.json`;
the source README declares that license for icons unless otherwise specified.
Artwork attribution: tgstation, Citadel and SPLURT/BlueMoon contributors; these
are conversions of the named source files, not newly authored artwork.

Database migration: `20261012120000_DeepLagoonBodyCustomization`, additive JSON
column for SQLite and PostgreSQL. Migration rollback removes this cosmetic data.
