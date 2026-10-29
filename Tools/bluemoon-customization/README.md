# BlueMoon character customization

Imports the active character accessory definitions from a local
MOLOT-BlueMoon-Station checkout into SS14 markings. Requires Python, Pillow,
PyYAML and `rg`. The BlueMoon checkout is read only.

```powershell
python Tools/bluemoon-customization/import_assets.py C:/old_disk/repos/MOLOT-BlueMoon-Station --report .local/codex/bluemoon-customization/import.json
python Tools/bluemoon-customization/validate_assets.py
python Tools/bluemoon-customization/validate_alignment.py C:/old_disk/repos/MOLOT-BlueMoon-Station
python -m unittest discover -s Tools/bluemoon-customization -p test_import_assets.py -v
node Tools/bluemoon-customization/hair-browser.test.cjs
```

`--refresh-existing` updates frames in the existing catalogue without changing
prototype IDs, localization, selections or deduplication. It checks source
revisions, state lists and output paths before writing; combine it with
`--audit` for a read-only comparison.

`--audit` computes the import without changing shipped resources. `--inventory`
outputs the resolved source definitions instead. Reports must stay in `.local`.
The browser test uses Playwright and headless Edge; `PLAYWRIGHT_MODULE` can point
to a separately installed Playwright module. `HAIR_SCREENSHOT` optionally saves
a screenshot to a local diagnostics directory.

The initial import uses BlueMoon revision
`e9ece923dd956fb78ae6cb83e1eaaf259b61a432`: 2337 marking prototypes, including
398 hairstyles, 8 facial-hair styles and 1931 body markings/accessories. It skips
567 duplicate source entries. The remaining nonempty named definitions without
usable frames are `insect`, `synthlizplates`, `vox_snout`, `tesh_beak` and
`tesh_beak_alt`; placeholders and unnamed parent definitions are also omitted.
Known source-path errors and state-name typos have explicit `SOURCE_FIXUPS`.

Duplicate detection compares all directions and animation frames, ignores RGB
under transparent pixels, and recognizes reshaded hair silhouettes and existing
unrestricted hair-state identities. Existing prototype IDs remain unchanged.
Male/female torso variants stay separate; limbs and palette regions remain
independently editable. Blank states are skipped. Each RSI records the source
file, revision, contributors and CC-BY-SA-3.0 license. Style names retain their
original searchable spelling in both locales.

DMI frame-major order is converted into RSI direction-major order, retaining
animation delays. Large uncentered overlays receive padding on the left and/or
bottom to keep BYOND's bottom-left origin at the center of the RSI frame.
Centered overlays use the source definition's `dimension_x`/`dimension_y`,
including cases where those dimensions differ from the DMI frame size.
`validate_alignment.py` independently compares every large source frame's alpha
mask in world coordinates with the corresponding shipped palette layers, across
all directions and animation frames. Palette masks become grayscale tintable regions.
`spriteLayersBefore` anchors rear overlays before the body/head; ordinary
`spriteLayers` anchors front overlays after their body part. Tail front overlays
and unsplit tails use the native Tail anchor, above the torso, arms, hands and
legs; split rear overlays remain behind the torso. Cosmetic wings use
the Chest body part so ordinary humanoid species can render them. This import
adds cosmetic choices, without flight or other BYOND gameplay mechanics.

The editor filters tails (including tail extras), snouts and wings by their
native marking categories. Ears have explicit `editorType: Ears` metadata:
render layers such as HeadSide also contain frills and synthetic screens, so
the catalogue must not infer ears from a render layer or a translated name.
Explicit types also cover harpy wings stored as arm extensions, muzzle markings
stored on the head, and tail decorations stored as general overlays.
The importer retains the source `ears` classification when generating resources.

Output is limited to `Resources/Textures/_DeepLagoon/Customization/BlueMoon`,
`bluemoon.yml` and the two locale files. Rerunning regenerates these resources
and removes obsolete importer-owned RSI directories only after checking their
resolved paths, attribution and contents.

The client supplies one cached face image from the preview dummy's actual head,
eyes and selected head markings. Hair/beard catalogues keep using lazy sprite
IDs; face pixels do not receive the selected hair tint. Each carousel has its
own local fuzzy search and resets scrolling when the search changes.

`species_rules.py` generates `species_rules.yml` from the user-approved family
assignments. Existing native species assignments are retained, ordinary scars,
tattoos and geometric markings belong to the shared Common catalogue category,
and unmatched BlueMoon anatomy requires the free `GeneticModification` trait.
Synth always has access to every species' marking set. The trait bypasses only
species restrictions; sex-specific art and existing positive selection limits
remain validated. Categories with zero species points become available with the
trait or for Synth. Removing the trait removes incompatible draft selections.
Run `python Tools/bluemoon-customization/species_rules.py` after editing families
or adding new markings; a full asset import also regenerates the rules.
