# BlueMoon character customization

Imports the active character accessory definitions from a local
MOLOT-BlueMoon-Station checkout into SS14 markings. Requires Python, Pillow,
PyYAML and `rg`. The BlueMoon checkout is read only.

```powershell
python Tools/bluemoon-customization/import_assets.py C:/old_disk/repos/MOLOT-BlueMoon-Station --report .local/codex/bluemoon-customization/import.json
python Tools/bluemoon-customization/validate_assets.py
python -m unittest discover -s Tools/bluemoon-customization -p test_import_assets.py -v
node Tools/bluemoon-customization/hair-browser.test.cjs
```

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
animation delays. Large uncentered overlays receive transparent padding to keep
BYOND's bottom-left origin. Palette masks become grayscale tintable regions.
`spriteLayersBefore` anchors rear overlays before the body/head; ordinary
`spriteLayers` anchors front overlays after their body part. Cosmetic wings use
the Chest body part so ordinary humanoid species can render them. This import
adds cosmetic choices, without flight or other BYOND gameplay mechanics.

Output is limited to `Resources/Textures/_DeepLagoon/Customization/BlueMoon`,
`bluemoon.yml` and the two locale files. Rerunning regenerates these resources
and removes obsolete importer-owned RSI directories only after checking their
resolved paths, attribution and contents.

The client supplies one cached face image from the preview dummy's actual head,
eyes and selected head markings. Hair/beard catalogues keep using lazy sprite
IDs; face pixels do not receive the selected hair tint. Each carousel has its
own local fuzzy search and resets scrolling when the search changes.
