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
- Surveys are campaign-wide knowledge. They are not a personal currency or donation reward.

Asteroid and wreckage profiles currently describe **environmental exposure fields**.
They do not yet generate asteroid grids, wreck maps, resource mining or a persistent ship.
The first interactive encounter is surveying; rescue/repair/branching encounters remain
future content on this foundation.

## Configuration and operations

Server CVars:

- `sector.environment_enabled` (true): enable new assignment and environmental effects.
- `sector.campaign` (`frontier`): stable save namespace. Choose before loading sectors.
- `sector.max_loaded` (8, clamped to 1-64): maximum simultaneously instantiated sectors.
- `sector.world_seed` (20261029): seed for newly visited sectors; existing manifests win.

Client `render.sector_environment` only disables cosmetic clouds, not radar warnings or gameplay.

Mapping/admin command (creates/loads a sector map; never transfers a player or ship):

```
dl_sector_create expedition/1 719 DeepLagoonAsteroidCloud
```

Profiles: `DeepLagoonFrontier`, `DeepLagoonAsteroidCloud`, `DeepLagoonWreckage`,
`DeepLagoonRadiationSea`, `DeepLagoonQuietReach`.
Repeated creation of an already loaded identity returns that map; reopening a saved identity
uses its original manifest, even if a different seed/profile was supplied.
Use `dl_sector_unload <map-id>` after transferring all ships and players. It refuses
to delete a sector containing any grid/player; manifests and surveys remain on disk.
There is no timer-based deletion of occupied sectors. The future travel layer should
unload an old empty map after a successful transfer.
Generated maps are FTL destinations, subject to normal console/engine travel restrictions.
The endpoint is checked after the existing range clamp.

## Durable state contract

`space-sectors.db` resides in the server's user-data directory. Back it up with the server
stopped, or use SQLite's online backup API (include the WAL when copying an open database).
No existing production/account database schema is changed.

`SectorStore` stores an immutable full manifest, not just a seed. Campaign + stable sector ID
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

- At most 80 regions and 8 planets per generated sector; invalid profiles are rejected.
- Regions are descriptors, not per-particle entities. No permanent debris physics storm.
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
