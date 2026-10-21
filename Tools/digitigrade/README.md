# Digitigrade legs and clothing review

Vulpkanin, Feroxi and Tajaran use the four leg/foot states from Arcane. Other
body states, species abilities, inventory slots and movement are unchanged.
Arcane's `_Goobstation` Feroxi assets are intentional: its `_DV` copy still has
straight legs. Credits and CC-BY-SA-3.0 attribution are retained in RSI metadata.

Vulpkanin side-view leg roots are extended across the one-row gap to the native
torso after import. Only ten formerly transparent hip pixels are filled from the
adjacent thigh row; knees, feet and clothing coordinates stay in place. The repair
also runs without `--source`, so regeneration and reimport both retain it.
The same check found detached Tajaran thighs in the front/rear views; eleven
proximal pixels repair those connections without shifting the lower legs.
`EachLegJoinsTheNativeTorso` checks each leg's opaque connection to both torso
variants in all four directions for all three imported species.

Playable bodies and editor dolls inherit the same inventory maps. The reusable
inventory mixin must be the first parent because earlier parents take priority.
All map dictionaries explicitly include every affected slot, including on the
Feroxi specialization because a child dictionary replaces its inherited value: the clothing renderer
selects the sex dictionary as a whole, rather than falling back per slot.

Ordinary trousers use the existing `_White` digitigrade displacement. Feroxi has
two additional upper-thigh pixels in the side views. Footwear uses the same paw
map with the trouser mapping above the ankle so boot shafts do not vanish.
Outer garments only deform below the waist. Coats keep their hanging hems;
hardsuit and EVA bases opt into bending their trouser legs.

`Clothing.preserveLegHem: true` selects `DisplacementData.hemMaps` when provided,
falling back to normal `sizeMaps` otherwise. Both skirt bases use this flag;
four skirts that inherited the trouser base now inherit the skirt base. A new
unusual robe/skirt should set this field or inherit a suitable base. A complete
outer suit with trouser legs should override `preserveLegHem: false`.

The existing ClothingBodyFitSystem composes the selected displacement with body
customization, preserving the original animation, tint, shader/stencil state and
cleanup path. Dedicated species clothing states retain their existing priority.
The new mechanism does not generate additional clothing texture variants per item.

Leg markings are generated from the original local assets retained under
`sources/`, with their original attribution in `credits.json`. Their lower pixels
follow the same maps and are clipped to the new leg/foot silhouette; upper-body
pixels are unchanged. This prevents old straight-legged markings from drawing
outside the new limbs. Full body/icon/husk illustration states are not used as
humanoid leg layers and remain their original illustrations.

## Regenerate and check

```powershell
python Tools/digitigrade/import_assets.py --source C:/old_disk/repos/ss14/arcane-master/arcane-master
dotnet test Content.Tests/Content.Tests.csproj --filter 'FullyQualifiedName~DigitigradeClothingTests|FullyQualifiedName~ClothingBodyFitTests'
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --filter FullyQualifiedName~ClothingBodyFitIntegrationTests
python Tools/digitigrade/build_review.py .artifacts/digitigrade-review
```

The source argument is only required to reimport limbs. Omit it to regenerate
maps and markings. Tests render both sexes of all three species in four directions,
checking closed trousers, identical undeformed hems, marking boundaries, footwear,
and composition with cosmetic anatomy. Existing body-fit regressions also run.
Integration tests equip/unequip trousers, skirts, boots, coats and hardsuits and
verify selected map states after sex changes, plus the existing body-fit lifecycle.

The generated review is an offline sprite/map comparison, not an in-game capture.
It omits cosmetic ears/tails, world lighting and client stencil effects. Visual
acceptance still requires the owner's review and an in-game check of representative
outfits, custom markings, anatomy sizes, and unusual layered clothing.
