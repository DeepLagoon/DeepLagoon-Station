# Shuttle flight limits and motion safety

Normal flight uses the navigation console's speed units. Powered linear thrusters
set a base-thrust-weighted part tier separately for each direction. The capacitor
rating is recovered from the native serialized thrust multiplier, including old
maps containing upgraded engines. One small upgraded engine cannot set the whole
bank to its own tier.

| Tier | Maximum speed | Minimum 0-to-maximum time |
| --- | --- | --- |
| T1 | 35 | 20 s |
| T2 | 45 | 15 s |
| T3 | 55 | 10 s |
| T4 | 65 | 8 s |
| T5 | 75 | 6.4 s |
| T6 | 85 | 5.12 s |

Further tiers add 10 speed units each. Above T4 the acceleration time is
`max(2, 8 * 0.8^(tier - 4))` seconds. Insufficient thrust or damping can make
acceleration slower. Pilot boosts and engine count cannot exceed the acceleration
ceiling. Brakes retain their existing multiplier. The pilot's speed setting can
lower the envelope, including zero, and diagonal thrust uses a vector speed limit.
The old exponential speed resistance is replaced by this envelope.

A server-side cap also covers drift without a pilot and engine loss. Docked grids
share the highest member envelope so an unpowered towed grid cannot slow a powered
ship to T1; each driving ship still has its own thrust limits. FTL travelling and
arrival are exempt; startup and cooldown are normal flight.

The accompanying RobustToolbox change refines physics steps when a moving or
rotating grid has a possible nearby collision. Relative surface travel is sampled
at most every 0.5 units, up to 64 steps per tick. Empty-space flight retains the
configured step count. This is bounded discrete stepping, not a general TOI solver
for arbitrary admin velocities or sub-tile collision geometry.

Impact mass sampling and damage zones are centered on the struck tile's center,
including its tile size. A small impact radius previously centered on the tile's
corner could omit the struck tile entirely and leave the exterior undamaged.

The client camera uses the same rendered grid rotation as sprites. The engine UI
scaling fix rejects non-positive/non-finite DPI and invalid autoscale ranges.

## Applying the engine fix

Engine implementations belong in `C:\old_disk\repos\RobustToolbox`. The
Station gitlink must point to the reviewed flight-safety commit; rebuild the server
and content against that revision. The compatibility contract remains official
engine 292.0.1 with the launcher's signed WebView 288.1.0. Authentication, wire
formats, loader trust, module signatures and engine identity are unchanged.

An installed official client keeps its own engine binary: the UI scaling fix only
runs in a rebuilt engine and requires an official engine release to reach ordinary
official-launcher clients. A content build or sandbox pass cannot establish that
this engine fix has shipped. Live rotation comfort, client login/TGUI and server
load under multiple simultaneous impacts still require in-game verification.
