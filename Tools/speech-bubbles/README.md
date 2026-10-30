# Speech and typing indicators

WhiteMoon separates active typing (`bubble_icon0`) from thinking (`bubble_icon3`).
`code/modules/client/verbs/typing.dm` returns to thinking after five seconds without
input. MOLOT's `code/modules/mob/typing_indicator.dm` instead creates an overlay
when opening the prompt and removes it on completion; its fallback timeout is
20 minutes. MOLOT distinguishes emotes and overrides Say indicators in
`code/game/objects/items/devices/radio/headset.dm` for command and department heads.

DeepLagoon keeps its native appearance/RSI system. Say, whisper and radio use the
speaker's species indicator or the equipped headset style. The default Say uses
MOLOT's `normal_typing` pixels and timing. Emotes use MOLOT's separate action
cloud; LOOC uses the same normal sprite, offset and timing as Say, tinted
turquoise (`#40e0d0`). Private/global channels do
not reveal a local typing indicator. Headset styles cover command, medical,
engineering, science, cargo, security, service, CentCom and freelancers, including
inherited Common/over-ear variants. A headset override takes priority over hats.
Ordinary headsets preserve species visuals.

Both native and TGUI input return to a static idle frame after **five seconds**
without text changes. Empty input is idle immediately. Losing focus, submitting,
closing the composer, or disabling indicators hides the layer. TGUI drafts flush
at most every 180 ms even during uninterrupted input. The network sends state
transitions rather than every keystroke. Equipment changes refresh the layer.

The pre-existing Idle sprites in `Effects/speech.rsi` are themselves animated;
switching to those states alone never stopped animation. The visualizer now
explicitly disables automatic animation and selects frame zero in every idle
state, then enables animation again on resumed input.

## Assets

Regenerate the imported RSI with Python/Pillow:

```powershell
python Tools/speech-bubbles/import_assets.py C:/old_disk/repos/MOLOT-BlueMoon-Station
```

The importer reads `icons/mob/talk.dmi`, preserves frame order/delays, removes
BYOND padding without resizing the pixels, and extracts static idle frames. The
two-pixel vertical padding is compensated by the prototype offset. The RSI
metadata records the source revision/hash and CC-BY-SA-3.0 attribution, matching
MOLOT's README asset license. The source checkout stays read-only.

## Validation

`Content.Tests.Client.TypingIndicatorTest` covers timeouts, input clearing,
channel changes, focus/submission, disabled indicators and clothing priority.
`TypingIndicatorVisualTests` checks native sprite states, stopped/resumed
animation, and inherited headset prototypes with loaded game resources.
Run the client sandbox tests against rebuilt client/shared assemblies too.

`TguiTypingIndicatorTests` drives composer actions through the native chat
callbacks, checks the server's replicated typing state, and verifies that the
player's sprite layer animates for Say/emote/LOOC. TGUI draft writes must opt in
to `LineEdit.SetText(..., invokeEvent: true)`; otherwise the draft is saved without
raising the text-change notification that starts typing. Browser bridge checks
alone cannot detect that native-side failure.

For manual verification, use two connected clients and check native/TGUI input,
long uninterrupted typing, a five-second pause, clearing the draft, changing
prefixes/channels, Escape, focus loss, and headset swaps while idle and typing.
Check species variants and tall/large character sprites for placement. Headless
tests do not establish an in-game visual sign-off.
