# LustWish

Adapted from the local WhiteMoon-Station checkout at
`634692776c7b38f998c12907bdbabe3aae46df65`, including the Skyrat, Zubbers and
ZZPLURT vendor extensions. The DM implementation is not copied into SS14.
Imported art is CC-BY-SA-3.0; each RSI records its source paths and revision.
`Tools/lustwish/assets.json` and `import_assets.py` reproduce the conversion.

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
goggles. The ordinary maid costume uses the existing SS14 mini maid outfit.
The two recolorable costumes and eight collar designs use a seven-color palette.
Held items have color verbs; attached items have color controls in the panel.
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

Action catalogs use `arousalGain`; the old `manaGain` field remains a fallback
for existing custom catalogs. No automatic gain is added to existing actions
that did not previously specify it. Reset messages remain private to the two
participants. No sound is configured for reaching capacity.

## Paired interaction portals

Use `DLLustWishPortalKit` in hand to place one endpoint at your position.
Use it again elsewhere to place its linked endpoint and consume the kit.
Through the context menu, a participant chooses **Connect to portal** and stays
within ordinary accessible interaction range. Someone at the opposite endpoint
can choose **Interact through portal** to open the existing interaction panel.
Both sides may connect. Only one participant may register at each endpoint.

This preserves the source's remote interaction concept without teleporting
people or implementing anatomy projections. Normal living-player and category
consent checks in the interaction panel continue to apply, including its
existing development-build debug override. Every action/repeat rechecks reach.
Disconnecting, moving out of accessible range, becoming unable to interact,
or destroying an endpoint revokes the relay. Deleting an endpoint clears the
surviving peer's references. Remote interactions do not play movement animations
between the actual characters. There is no anonymous-mode identity substitution.

## Equipment tab

The **Accessories** tab is available in both TGUI and the native fallback. Hold an
accessory in the active hand and select Attach. Accessories occupy separate
neck, wrists, ankles, hands, head, eyes, ears and torso containers, leaving normal
SS14 inventory slots available. The attached item remains a real entity; its worn
sprite and palette replicate to clients. Ordinary costumes use native clothing
slots and the checkout's HELMET / INNERCLOTHING state names.

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
chest/genital growth. Reduction never creates missing features. Eight 30-unit
bottles are sold using these existing reagents. The chemical-change preference
continues to block creation/resizing and clear unfinished doses; saved character
profiles and editor limits remain separate from runtime changes. Gender changes,
source-specific physiological side effects and anatomical simulation are excluded.

## Remaining dependencies

- Insertion, organ-attached toys, anatomical relays, fluids/protection and
  transformation physiology require further anatomy systems. The remote massager
  is an external torso accessory, and serviettes use native puddle cleaning.
- Mind/size/tracking collar variants, transforming Chief Screws plush, NIF software,
  MOD modules and cyborg upgrades are not ported as inert substitutes.
- Portal garments and body-projection relays are not included; the paired
  interaction-window portals remain the SS14 adaptation.

## Validation

The integration tests exercise catalog spawning, dose/recovery behavior,
portal lifetime, payment-card toggling, equipment consent snapshots, keys,
restraints, sensory effects, battery exhaustion, finite packs and kit deployment.
A connected-client check verifies replicated accessory layers and native clothing
tints. The existing apartment atmosphere test also builds and passes after its
missing mapping-extension import was corrected.
`TGUI/tools/lustwish-browser.test.cjs` checks the built interface at 420/360 pixels,
the purple meter, accessory controls and preservation of a custom-action draft.
Use `PLAYWRIGHT_MODULE` to point to an installed Playwright module when needed.
These automated checks do not replace a multiplayer visual/audio playtest.
