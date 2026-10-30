# Expedition progression

The server applies `DeepLagoonExpeditionLoot` from
`Resources/Prototypes/_DeepLagoon/Salvage/expedition_loot.yml` to newly generated expedition loot.
Existing visitor equipment is not scanned or confiscated. Other maps keep their original loot tables.

| Difficulty | Ordinary loot cap | Maximum generated cash note | Completion payment |
| --- | --- | --- | --- |
| Minimal | T1 | 100 | 1,000 |
| Minor | T1 | 100 | 2,500 |
| Moderate | T2 | 500 | 7,500 |
| Hazardous | T3 | 2,500 | 15,000 |
| Extreme | T3 | 2,500 | 25,000 |

Normal offers currently use Moderate, Hazardous and Extreme. The lower ratings remain supported
for debug consoles and future offer changes. Weapon, melee and explosive room markers are raised
to the ordinary tier of their expedition, up to T3; high-tier promotions remain blocked. Thus T3 is
the normal weapon table on Hazardous/Extreme expeditions, not just a possible upgrade from T1.
The cash-note limit applies to generated notes,
not to the separate completion payment or the total value of a raid.

## Rare loot

Only Extreme expeditions roll a rare entitlement. One roll is made when the map is generated:
T5 has probability `1 / 720`, T4 has probability `1 / 168`, and otherwise there is no entitlement.
These are disjoint intervals, so there can be at most one rare award per expedition.

At 24 eligible expeditions per day across the server, the nominal intervals are 30 days for T5
and 7 days for T4. These are statistical averages, not calendar cooldowns. Parallel crews and
changes in the actual number of Extreme expeditions change the intervals. Failed expeditions
can lose an unclaimed entitlement. Tune the two profile probabilities using the server's actual
eligible expedition count; player count alone does not determine that count.

An NPC whose original equipment/loot includes a weapon of the rolled rare tier can consume the
entitlement on its first death. All ordinary equipment and unopened NPC bundles still use the
normal cap, so a guaranteed MR-8C/Atreides in the old bundle is downgraded. Repeated death events,
more NPCs and opening the bundle later do not roll additional rare awards. If no NPC consumed the
entitlement, a successful expedition receives one reward from the corresponding pool at its console.
Cash-payment configuration does not disable the rare reward.

T4/T5 have no ordinary source. In particular, Bulldog is T5 despite its former duplicate T3 entry;
restricted explosive contents and weapon blueprints are classified alongside their cases.
T4 stock parts are rare awards rather than common T3 drops. New unclassified item guns default
to T5 and downgrade to the ordinary weapon progression until explicitly classified.

## Provenance and coverage

`ExpeditionLootOriginComponent` is attached before MapInit and copied through native spawning.
It records the cap independently of the expedition map, so taking a bundle home and deleting the
map does not restore the old contents. Native position, rotation and container semantics are retained.

The policy covers dungeon prefabs, conditional/random/entity-table spawners, storage fills,
open-triggered containers, items spawned on use, count spawners, destruction and timed despawn.
NPC starting gear is copied and filtered before the normal LoadoutSystem runs; global prototypes
are not mutated. Vending inventories, restocks and dispensing also retain the cap after transport.
Blueprints that manufacture T4/T5 weapons are not included in the ordinary blueprint loot.

Rare reward containers propagate only their explicitly awarded tier to their own contents.
The ordinary expedition cap is not raised when a rare item is awarded.

## Crates and NPC navigation

Crates use the existing Climbable/Climbing mechanics and their normal collision layers. Expedition
NPCs get NavClimb and Climbing. Steering detects nearby movable climbable obstacles across the
route as well as static navigation nodes, and uses the native timed vault. It does not teleport the
NPC, delete the crate or turn off collisions globally. Path cost prefers climbing to demolition,
and obstacle handling validates reachability against the actual obstacle entity.

Regression tests are in `ExpeditionLootBalanceTests` and `ExpeditionLootIntegrationTests`.
They cover rarity boundaries, replacement closure, nested loot, delayed opening, NPC/completion
idempotency, visitor equipment, vending restocks and physical traversal of a narrow crate-blocked
corridor with both movable and anchored crates.
