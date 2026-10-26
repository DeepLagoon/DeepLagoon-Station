# LustWish

Adapted from the local WhiteMoon-Station checkout at
`634692776c7b38f998c12907bdbabe3aae46df65`, including the Skyrat, Zubbers and
ZZPLURT vendor extensions. The DM implementation is not copied into SS14.
Imported art is CC-BY-SA-3.0; each RSI records its source paths and revision.
`Tools/lustwish/assets.json` and `import_assets.py` reproduce the conversion.
`items.json`, `import_items.py` and `source_metadata.json` preserve original
names and descriptions, including inherited DM metadata. Names remain original
in both locales. Russian descriptions come from `descriptions.ru.json`; the
importer validates full coverage and retains the translation on reimport.
Descriptions that were empty in WhiteMoon remain empty. English descriptions
and raw prototype metadata retain the source text. `slot_mapping.json` documents
the source-to-SS14 slot mapping.
Run `python Tools/lustwish/verify.py` for an offline metadata, slot and RSI check;
run `python Tools/lustwish/import_items.py <WhiteMoon checkout> --check` to compare
against the pinned source itself.

## Placing and using the machine

Place `VendingMachineLustWish` through mapping or administrative spawning.
`VendingMachineRestockLustWish` refills this inventory only. Existing SS14
power, breakage, repair, wiring, cash/bank payment and inventory depletion apply.
No existing map is changed.

`DLLustWishEliteCard` toggles free vending when used on the machine. Swiping it
again restores its original payment setting. The card is not sold by the vendor.
Prices use the SS14 economy instead of BYOND departmental paychecks. The native
emag inventory contains the five premium Starlight Singer costume pieces and
the sinister vox plushie (using SS14's standard squeak behavior);
cutting the contraband wire exposes the Jabroni outfit.

## Assortment

The ordinary inventory retains source stock counts for chaps, black/white bunny
suits, rabbit/kitty ears, cap, heels, evening gloves, harness, knee boots, latex
suit/half-suit, corset, latex gloves/heels/stockings, maid headband/uniform/latex
uniform, blindfold, leather/cloth loincloths, open-toed boots, pillow, leather
storage belt, padded handcuffs, massager, feather, cyan stage outfit and decorative
goggles. The maid costume uses its imported source prototype.
Recolorable clothing grants a **Change color** action while held or equipped.
The normal action bar opens a native color wheel with brightness and Apply.
The server accepts opaque RGB colors and verifies that the player still holds
or wears the item. The Accessories tab has no palette dropdown.
Species-specific BYOND overlays and shape switching are not carried over. The blindfold blinds; handcuffs restrain hands using the
native cuff system; the belt stores items.

The massager and feather act on the holder on use, with a three-second delay.
They add 8 and 4 arousal respectively. They do not target body parts or activate
on another character. Crocin pills/bottles contain 10/30 units and raise arousal
by 6 per unit metabolized. Camphor pills/bottles contain 10/30 units and lower
it by 12 per unit. Partial metabolism doses scale proportionally. Source stock
counts are preserved: 20/10 pills and 6/3 bottles. Eight paired portal kits are
available.

## Arousal

The old interaction-panel mana system is replaced by
`InteractionPanelArousalComponent` and `InteractionPanelArousalSystem`.
Configuration is in `interaction_panel_arousal.yml`. Capacity still follows the
existing health-threshold rule. The meter loses one point per second; filling
it resets it to zero and starts ten seconds of recovery, during which positive
adjustments are ignored. Negative adjustments clamp to zero. The local player's
existing vignette now uses pink tones. No preferences are modified.

Interaction Panel sex restrictions accept either the selected body Sex or enabled
primary body features. Penis/testicles add Male compatibility; vagina/womb add
Female compatibility. Disabled features and secondary features do not contribute;
hiding a feature does not remove it. Both compatibilities can coexist. Unsexed
still refers to the selected Sex, and gender/pronouns are unaffected. Initiator
and target restrictions are checked independently. The server revalidates every
request, consent response and repeat; appearance changes update an open panel
without replacing its window.

Action catalogs use `arousalGain`; the old `manaGain` field remains a fallback
for existing custom catalogs. No automatic gain is added to existing actions
that did not previously specify it. Reset messages remain private to the two
participants. No sound is configured for reaching capacity.

## Paired interaction portals

Click an anchored wall with the held `DLLustWishPortalKit` to mount an endpoint
on the facing surface. Click another wall to mount the peer and consume the kit.
Floor clicks and using the kit in hand do not place endpoints. Endpoints are
children of their supports, preserving placement on moving and rotating grids.
Breaking, deleting or unanchoring a support removes its endpoint and releases
the participant immediately. The surviving peer becomes disconnected.

Drag a character onto a complete portal pair to use the native chair buckle
flow. One character occupies the pair. Their upper sprite half remains at the
entrance; a replicated copy displays the lower half at the exit. Native clothing,
color and appearance layers are copied. Unbuckling restores the complete sprite
and removes the projection. Losing a support releases and reparents the character
before the support's children are deleted.

The Interaction Panel opens on the actual body or the projected half, and keeps
its existing category consent checks. Both endpoints grant ordinary local reach
to that body; releasing or destroying an endpoint revokes remote reach.
Speech, whispers and positional sounds take the shortest direct or single portal
route, while remote view subscriptions deliver the required entities. The camera
remains on the player's normal eye. There is no anonymous identity substitution.
Equipment operations still require physical reach and cannot move items through
the portal.

## Equipment tab

The **Accessories** tab is available in both TGUI and the native fallback. Hold an
accessory in the active hand and select Attach, or equip clothing through normal
inventory controls. Collars use neck/neck2/neck3 (the same NECK slot flags as
cloaks); mittens use gloves, hoods head, goggles eyes, headphones ears/head/neck,
and leashes the belt slot. Costumes use the corresponding native clothing slots.
Only non-clothing attachments such as wrist/ankle ropes and the external torso
device retain separate accessory containers. Native removal clears effects, and
native attempts to dress another player use the same consent setting as the tab.
Worn sprites use the checkout's HELMET / INNERCLOTHING / HAND state names.

Actions on another character use the interaction panel's Erotic consent setting:
No rejects, Ask requests explicit approval, Yes permits the action. Equipment does
not use the development-build consent override. Approval is bound to the recipient
session, exact accessory, active held item, operation and current range. It expires
after 20 seconds. Requests are limited to one pending request per target and one
request per actor/target every five seconds. Physical operations need an open,
unexpired panel and ordinary accessible reach; portal reach does not permit
moving real equipment through a wall. The wearer can always release their own
accessories immediately, or escape one item with a ten-second interruptible action.

A blank key binds an unbound lock on its first lock operation. Applying an already
bound key to a blank key copies it. A different key cannot unlock the accessory.
Mittens and wrist ropes block pickup, use, attacks and ordinary interactions;
ankle ropes halve movement speed. Generic container removal cannot bypass equipment
operations. Removing or deleting an accessory clears its effects.

Hold a leash and attach it to a neck accessory. This starts native SS14 pulling;
it does not teleport or force movement through obstacles. It releases when the
holder drops the leash, pulling stops, participants become invalid, consent is
revoked, or distance exceeds three units. A purple line is visible to the two
participants. An unlocked collar can be removed normally; the wearer's emergency
release also clears the leash and lock.

Pair the held remote with an attached electronic item. Use the remote to toggle
it; its context verbs change intensity (1–3) or pulse an electric collar. Remote
commands need the same map and a maximum range of 32 units. They recheck consent,
recipient and held controller. Electronic equipment uses an ordinary replaceable
PowerCellMedium, draining 1–3 charge per second while enabled. The torso massager
adds 2–6 arousal per second while powered. The electric collar's manual pulse costs
10 charge, stuns for half a second, and has a ten-second cooldown. It does not deal
damage. Running out of charge disables continuous effects.

Hypnotic glasses provide a focus vignette and a 10% movement slowdown when enabled;
they do not issue commands or take control of a player. The sensory hood blinds
through native Blindable checks. The hood and headphones dampen positional world
audio by 30 dB and hide other speakers' local speech and whispers. OOC, emotes,
radio text and UI audio retain their usual channels. Removal restores hearing and
vision without changing client sound preferences.

## Consumables and construction

A serviette pack dispenses exactly four wipes. Each wipe uses native puddle
absorption (up to 10 units) once, then becomes a used wipe. Empty packs cannot
produce further contents. These are cleaning supplies; no anatomical fluid or
protection system is introduced.

The padded bed, cross stand, rope stand and dance pole have single-use assembly
kits. Use the held kit on an unobstructed grid tile and remain still for three
seconds. Completion rechecks placement and consumes the kit. Beds and stands use
native straps and can be buckled/unbuckled normally. An empty structure can be
packed into its kit with a screwdriver using its native construction graph;
occupied straps prevent packing. Native damage/destruction remains applicable.

## Size chemistry

The eight existing DL growth/reduction reagents and recipes retain their IDs.
Metabolism is now 0.25 units per cycle, matching WhiteMoon's numeric rate. Five
units still change one catalog size stage: 20 cycles at this rate. Partial doses
count by actual consumed volume; opposite signed doses still share Station's
per-feature accumulator. Cycle timing follows SS14's metabolizer.

Normal growth only resizes an existing enabled feature. As in the source's
overdose creation path, a missing or disabled feature starts at minimum size when
the remaining growth concentration reaches 17 units for belly/butt or 20 units for
chest/genital growth. Reduction never creates missing features. Four original growth bottles are sold using these existing reagents.
The four Station-only reduction bottle prototypes remain compatible with saved
maps, but are no longer substituted into the source vendor catalog. The chemical-change preference
continues to block creation/resizing and clear unfinished doses; saved character
profiles and editor limits remain separate from runtime changes. Gender changes,
source-specific physiological side effects and anatomical simulation are excluded.

## Remaining dependencies

- Insertion, organ-attached toys, anatomical relays, fluids/protection and
  transformation physiology require further anatomy systems. The remote massager
  is an external torso accessory, and serviettes use native puddle cleaning.
- Mind/size/tracking collar variants, transforming Chief Screws plush, NIF software,
  MOD modules and cyborg upgrades are not ported as inert substitutes.
- Portal garments and organ-specific attachment relays are not included. The wall
  portals use a native buckle and a split character sprite instead.

## Validation

The integration tests exercise catalog spawning, dose/recovery behavior,
portal lifetime, payment-card toggling, equipment consent snapshots, keys,
restraints, sensory effects, battery exhaustion, finite packs and kit deployment.
A connected-client check verifies native clothing tints and the color action.
The portal regression exercises wall-only placement, native drag/drop, remote
reach and speech, positional audio delivery, split-sprite replication, grid movement
and support lifetime.
`TGUI/tools/lustwish-browser.test.cjs` checks the built interface at 420/360 pixels,
the purple meter, accessory controls and preservation of a custom-action draft.
Use `PLAYWRIGHT_MODULE` to point to an installed Playwright module when needed.
These automated checks do not replace a multiplayer visual/audio playtest.
