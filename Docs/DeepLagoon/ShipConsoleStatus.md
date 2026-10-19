# Ship console status

The shuttle, gunnery and mass-scanner TGUI windows share the common console housing with a matte graphite screen
and rectangular native radar viewports. The separate handheld scanner retains
its circular aperture.

The shuttle console's **Состояние** tab renders an abstract design footprint,
including missing parts, rather than live sprites or physical holes. Missing
slots are red. Building at a new position expands the baseline; a replacement
of the same category at the original position repairs that slot. The baseline
is local to the shuttle grid and lasts until that grid is removed.

Integrity counts intact slots relative to expected nonempty floor tiles,
anchored thrusters/gyroscopes, ship weapons and destructible/airtight structures.
It does not average entity hit points. Intentionally dismantling or relocating
equipment also leaves a missing slot at its former position.

Working propulsion uses `ThrusterSystem.CanEnable`, including power, enabled
state and nozzle clearance. Angular thrusters are reported as gyroscopes.
Stored energy and capacity sum anchored `PowerNetworkBattery` entities with a
`Battery` on this grid, including SMES, substations and APCs. Loose cells and
storage on another grid are excluded.

Server snapshots run every two seconds. Only changed state is pushed to open
interfaces. The client receives at most 64 by 64 schematic cells and renders
one canvas without an animation loop or per-entity DOM elements. This bounds
client schematic work independently of the ship's entity count; it does not
constitute a measured in-game frame-rate guarantee.

New losses trigger in-world speech from powered,
anchored consoles: **Повреждено X% шаттла!** The percentage is current total
loss rounded up. Losses are coalesced with a six-second announcement cooldown;
unchanged losses do not repeat, and complete repair clears a pending warning.

Focused checks live in `ShuttleHullBaselineTests`, `ShuttleHullLossAlertTests`,
`ShuttleHullConsoleTests`, `ShuttleCombatConsoleTests`, and
`TGUI/tools/shuttle-console-browser.test.mjs`. In-game checks should include
audibility/speech, large hulls, construction, repairs and power loss.
