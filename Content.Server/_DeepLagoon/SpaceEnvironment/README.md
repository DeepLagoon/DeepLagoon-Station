# Space environment and persistent sector foundation

The current implementation works inside Station and extends its FarHorizons star system.
It does not depend on LuaWorld or introduce engine/client trust changes.

## What is active

- The first loaded Kyphrus game map gets a persistent manifest in the `frontier` campaign.
- On first assignment, environmental fields overlapping the initially loaded stations are excluded before the manifest is saved.
- The existing five planets retain their templates, names and coordinates. Appearance seeds
  vary rotation, terrain frequencies, cloud coverage and colour within each template.
- Both shuttle radar and map draw bodies and named environmental regions. Known bodies
  remain visible with IFF switched off; radar shows edge indicators for distant bodies.
  Planet portraits share the exact procedural shader setup with the space background, including
  generated palettes, terrain, atmosphere, lighting and rings. On-screen portraits have a minimum
  diameter of 32 UI pixels (scaled with interface DPI) and a maximum of 140. Only visible portrait
  quads run the shader; instances are shared by both navigation controls and disposed with their map.
- Environmental regions have translucent, softly shaded interiors on both navigation views.
  Their colour follows the region type, density follows intensity, and the seeded shading
  remains stable while panning/rotating. Fills stay inside the gameplay boundary and below
  contours, labels and ship markers, including where regions overlap.
- Dust/asteroid/debris regions permit safe slow travel (12 m/s). After an eight-second
  grace period, faster ships receive a bounded hull hit at most once per five seconds.
  Active powered ship shields absorb hits using their existing damage/load accounting.
- Plasma prevents FTL preparation and arrival inside its ellipse; intense plasma jams radio.
  It does not damage a ship merely because a hyperspace line passes across the ellipse.
- Radiation uses the native radiation receiver pipeline, including personal/container
  protection. It is a uniform environment contribution, not a simulated all-direction
  ray field through the hull. A grid `SpaceEnvironmentProtection` can provide attenuation.
- The console context menu offers `Scan space environment`. At 1800 m it detects the
  nearest unfinished survey, inside the inner region and below 4 m/s it records completion.
  The 20 second cooldown belongs to the ship, shared across consoles and crew.
- Surveys are campaign-wide knowledge. Only the first successful journal entry awards RnD.
  The default reward is ceil(6500 * (1 + intensity) * (1 + radius * sqrt(aspect) / 1000)).
  6500 is the base artifact-node value; equivalent radius accounts for the ellipse's area.
  Valid regions yield 6825-39000 points with the default base. Larger and more intense regions
  always increase the reward. Journal uniqueness prevents repeat rewards after unloading/reloading.
- Rewards go to the console's explicitly connected research server, or the unique server on
  the same ship grid. Missing/ambiguous servers produce a portable research disk instead.
  Full server counters also fall back to a disk. Disks are handed to the explorer, or left by
  the console if their hands are occupied; they use native RnD disk redemption and have no sale value.
  Existing RnD balances and items use their native persistence rules; the journal is not a fleet snapshot.

Profiles now target 72 frontier, 120 asteroid-cloud, 104 wreckage, 96 radiation-sea and
32 quiet-reach regions. Placement remains bounded and excludes initial inhabited grids.
An append-only upgrade fills older saved manifests toward the new target without changing
existing regions, planets or survey entries. Concurrent/stale updates cannot overwrite a newer manifest.

Space junk uses native scrap-wall grids. Normal piles fit within 4x4; debris and asteroid
fields allow footprints up to 10x10, with a maximum of 36 connected occupied cells per pile.
Standard Plating provides the visible subfloor, native wall anchoring and grid physics.
Walls include WallRockScrapPile, iron, quartz, coal and silver variants. Rich field piles also
include gold, plasma, uranium and occasional bluespace ore. Native blue NFBaseWreckDebris
loot tables provide 1-2 ordinary results, or up to 4 richer field results. Field piles have a
1-in-8 chance of one native salvage carp; no large random mob group is generated.
Asteroid-stream piles use a dedicated loot table: each loot selection has a 25% chance
to choose one ArtifactFragment1, with the remaining 75% using the normal rich field table.
The existing two field selections and four-result cap apply. Asteroid streams take precedence
where they overlap debris fields; other habitats retain their original loot tables.

Density follows the actual habitat at the predicted spawn position:
- Debris/asteroid fields: up to 10 local piles (depth adjusts the target between 6 and 10),
  one opportunity per second, larger piles, richer loot and fauna.
- Within 2 km of a planet: up to 3 small piles, one opportunity per 12 seconds.
- Open space: at most 1 small pile, an initial 30-90 second delay, then a 90 second interval.

Station grids, marked POIs (including mobile/unregistered ones), force-anchored POIs and
warp-point grids have a 1 km exclusion margin beyond their enclosing footprint. Both the
spawn point and the entire orbit must be clear. Existing managed piles are checked again
against moving/new POIs and a three-second velocity forecast, so orbital motion cannot
carry untouched junk into a station. PointOfInterestSystem marks every newly loaded POI.
Player-owned/deeded ships do not become exclusion zones merely because late joining gives
them a station entity or warp point. Explicit POI and force-anchor markers still take precedence.

Moving observers get new junk ahead of their map velocity, including the parent ship's
translation and rotation. The horizon has at least eight seconds of forward lead; newly
materialized approaching piles reserve their estimated approach time plus five seconds (maximum
90 seconds) to reach the observer envelope. Lead also accounts for junk speed and footprint.
An orbit is calculated backward from an encounter inside the predicted normal viewport,
including for stationary observers. Distant orbit anchors (at least 256 m) prevent invisible
small circles around the observer at a field center; a local 768 m fallback handles isolated fields.
Speeds above 500 m/s suppress new placement. Both paths exclude every other active eye/camera.

There are at most 100 managed junk grids per sector and at most 10 across all eyes of each
player. Overlapping observers share the physical population. Converging views retire excess
unclaimed distant piles instead of adding their counts. Sponsors receive even per-sector
shares (maximum 10 each), with unseen excess reclaimed first when more explorers arrive.
Spawn opportunities rotate through players, at most two grid creations per second globally.
For more than 100 observers in one sector, the remainder rotates every 30 seconds.

Untouched junk outside all conservative observer envelopes for 10 seconds is removed.
Boarding, cargo or construction retires ambient ownership and preserves an ordinary player
grid. Originally generated loose items/fauna are tracked by home grid. After leaving it,
objects outside all eye envelopes are removed on the next simulation update without a grace
timer. Items in hands/inventory or placed on an ordinary player grid become player property;
unrelated items are never swept up. Detached objects remain tracked even after their home
pile expires. A 120 second cell cooldown prevents immediate farming; its cache is bounded.

There are no full wreck maps or persistent fleet snapshots yet.
The first interactive encounter is surveying; rescue/repair/branching encounters remain
future content on this foundation.

## Configuration and operations

Server CVars:

- `sector.environment_enabled` (true): enable new assignment and environmental effects.
- `sector.campaign` (`frontier`): stable save namespace. Choose before loading sectors.
- `sector.orbital_debris` (true): materialize local space junk; existing untouched piles still age out.
- `sector.max_loaded` (8, clamped to 1-64): maximum simultaneously instantiated sectors.
- `sector.world_seed` (20261029): seed for newly visited sectors; existing manifests win.
- `sector.survey_research_base` (6500, clamped to 1-1000000): base RnD reward for future first discoveries.

Client `render.sector_environment` only disables cosmetic clouds, not radar warnings or gameplay.

Mapping/admin command (creates/loads a sector map; never transfers a player or ship):

```
dl_sector_create expedition/1 719 DeepLagoonAsteroidCloud
```

Profiles: `DeepLagoonFrontier`, `DeepLagoonAsteroidCloud`, `DeepLagoonWreckage`,
`DeepLagoonRadiationSea`, `DeepLagoonQuietReach`.
Repeated creation of an already loaded identity returns that map; reopening a saved identity
keeps its original bodies and regions, even if a different seed/profile was supplied.
Only an explicit append-only density upgrade may add areas.
Use `dl_sector_unload <map-id>` after transferring all ships and players. It refuses
to delete a sector containing any ordinary grid/player; unclaimed ambient debris is cleared first; manifests and surveys remain on disk.
There is no timer-based deletion of occupied sectors. The future travel layer should
unload an old empty map after a successful transfer.
Generated maps are FTL destinations, subject to normal console/engine travel restrictions.
The endpoint is checked after the existing range clamp.

## Durable state contract

`space-sectors.db` resides in the server's user-data directory. Back it up with the server
stopped, or use SQLite's online backup API (include the WAL when copying an open database).
No existing production/account database schema is changed.

`SectorStore` stores an append-only full manifest, not just a seed. Campaign + stable sector ID
is the key. `Version` identifies the manifest/generator/appearance contract; version 1
algorithms must remain available or get an explicit migration. Unsupported versions and
missing templates fail closed instead of regenerating visited terrain.

The survey ledger has a unique campaign/sector/region key. Repeated/concurrent completion
cannot create duplicate discoveries. Runtime map IDs and entity UIDs are never
persistent identities. No history-sized collection is loaded into memory.

The future travel layer can request `SectorSystem.GetOrCreateAt(long x, long y, profile)`.
Coordinates are logical addresses; generation is lazy and only bound maps get runtime caches.
`GetOrCreate(id, seed, profile)` and `Bind(map, manifest)` are the lower-level seams.

A fully persistent expedition still needs a separate versioned ship/fleet snapshot,
crew/account progression, a durable current-address checkpoint and atomic transfer/rollback.
Do not update the travel checkpoint before ship and player transfer has committed. This
foundation deliberately does not claim that saving a sector also saves a ship.

## Performance contract

- At most 160 regions and 8 planets per generated sector; invalid profiles are rejected.
- Regions are descriptors, not per-particle entities.
- Space junk: 100 managed grids per sector, 10 per player view (shared across cameras),
  at most 2 creations per second globally. Per-player shares and a rotating scheduler prevent
  one explorer consuming the sector pool. Claimed grids follow ordinary player-grid rules.
- Normal piles: at most 16 cells; field piles: at most 36 cells within a 10x10 footprint.
  At most one original fauna entity per field pile. No invisible distant population is simulated.
- Placement, POI exclusion refresh and gentle orbital correction run at 1 Hz. Visibility caps
  and detached-object cleanup follow simulation updates; observer buffers are reused.
- Debris cooldown cache is bounded to 4096 entries globally, expires after 120 s and is
  removed with the sector. Per-player map/habitat timers are also removed on sector unload.
- Cached radar contours per map (no per-frame trigonometric reconstruction).
- Cached radar fill meshes: 96 triangles per region, one draw per visible region, no particles
  or per-frame heap allocation. Navigation fills remain visible with cosmetic clouds disabled.
- One spatial index per loaded map, ship queries at 1 Hz, swept tests between samples.
- Long displacements fall back to the bounded region list instead of walking empty cells.
- At most one hull hit / 5 s / exposed fast ship; at most 256 children inspected per hit.
- A single inexpensive two-scale noise shader, maximum 12 visible region quads. No full-screen
  multi-pass nebula baking. Clouds remain inside the radar/gameplay ellipse.
- Manifests are networked on binding/change; survey lists only change on discoveries.
- Exposure state, indexes, scan cooldowns and loaded-map identities are removed on shutdown.

The map previews the direct course to the cursor (at most 5 estimates per second),
showing intensity-weighted dust/radiation distance, or endpoint interference in FTL mode.
The route evaluator is also an API for a future route-comparison panel, not an automatic navigator.

## Verification

Unit fixtures: `SpaceSectorTests`, `SpaceSectorStoreTests`.
Integration fixture: `SpaceSectorIntegrationTests`.
Rebuild the client/shared modules before `ClientSandboxTest`; test against an installed
launcher engine when available. Rendered official-client piloting and a long-session
performance comparison are separate from compilation/unit checks.
