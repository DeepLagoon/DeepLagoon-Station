# Crash recovery

Server-side crash insurance for character equipment, nested containers, installed implants,
cybernetic organs/parts, confirmed purchases and player-ship research balances. This is an
opt-in feature. Updating Station alone does not enable it.

## Configuration

```toml
[crash_recovery]
enabled = true
scope = "main"
budget_ms = 0.25
entities_per_tick = 4
max_pending = 4096
checkpoint_seconds = 900
```

Use a different scope for each independent runtime sharing a database. Do not change scopes
during a round. Enable in the lobby or before startup. The default is disabled.

Deploy the matching DeepLagoon_Control runtime helper and the Bot finance label before enabling.
Control must have its separate `admin.api_token`; the recovery shutdown endpoint does not accept
the bot's finance capability. No client protocol or engine changes are required.

## Storage and recovery

The existing Station `lagoon_economy_records` table stores versioned records under the separate
`crash-v1:<scope>:` namespace. Bank debits and their purchase receipts share a database transaction.
ATM deposits and consumed-cash tombstones share a transaction, so delayed snapshots cannot
resurrect already-deposited money. Discord is an audit view, never the recovery source.

Every run holds a 30-second writer lease, refreshed by the background writer. A replacement
process waits for this lease before treating an unclosed run as crashed. Explicitly closed runs
are never eligible. Settled/closed records expire after seven days; outstanding crash claims
remain available.

Equipment is attached to the immutable database `Profile.Id`, not to a character name, selected
slot or online session. The registry remains active when a user disconnects, ghosts, enters
either cryostorage implementation, or visits a sleeping apartment. Multiple bodies/characters
of an account remain separate. Apartment escrow carries its own owner and original return paths.
Temporary apartment kits and Bitrun avatars are excluded. Implants are restored through the
implant system; old bodies, wounds, mind entities and old action entity IDs are not restored.
Implant action charges, enabled/toggled state and remaining cooldown are reapplied to the newly
created action, so reinstalling an implant does not refill its uses.

The writer stores each entity independently using the engine serializer. Containment and item
identity are authoritative outside component snapshots. Reconstruction uses current containment,
which prevents old backpack snapshots from duplicating transferred contents. The persistent
apartment and parking stores are also checked before recovery, covering a crash immediately after
their own commit but before the recovery worker sees that commit.

Safety-deposit boxes also consult their authoritative database records. A stored box cannot be
reissued by crash recovery; a withdrawn box updates its withdrawal round in the same claim
transaction. A newly purchased box uses the purchase identity as its durable box ID.
Entity references carry immutable identities and are renumbered on reconstruction, so merging
claims from several incidents or crashing again before the next checkpoint cannot collide YAML IDs.
Issued item tombstones remain available when checking old purchase receipts, preventing another
crash from turning an already restored purchase into a refund candidate.

Recovery preserves currently existing runtime prototypes even when map migrations mark them as
obsolete for map imports. Genuinely missing prototypes retain their outstanding records while
other items can be prepared. A deferred claim retries independently of other characters.

Deserialization runs in the lobby before round start. Character joins during an active round
only transfer already-prepared entities, at most one equipment root per update. Available body
slots are reused; remaining items are delivered in a named recovery crate. A claim transfers
entitlements to the new run before materializing them, so another crash during delivery does not
consume the entitlement. A deleted/recreated character slot does not inherit another profile's claim.

Unspent research is recorded once per actual research server, not once per linked console.
Each lost balance produces a disk with the exact saved integer amount. Disk upload consumes the
disk and changes the research balance in one recovery batch. Pending point-disk print jobs also
carry entitlements. Research already retained by a parking snapshot is not paid again.

Refunds use actual confirmed purchase amounts. Restored, persistently stored, consumed, sold or
destroyed purchases do not also pay money. Undelivered or crash-lost purchases can refund once.
Bank balances are credited, never globally rolled back. The Bot publishes `bank_crash_refund`
through the normal bank channel.

## Normal shutdown

Round end, immediate round restart, scheduled server update, SIGTERM/SIGINT, CTRL+C and console shutdown close the run
before teardown. `EntitySystem.Shutdown` deliberately does not mark a run clean, because it can
also run during fatal teardown.

Control's root-owned runtime helper calls the loopback-only authenticated
`POST /deeplagoon/crash-recovery/close` endpoint before stopping/restarting an enabled live game.
An already-inactive game is not marked clean. Missing acknowledgement refuses the normal stop.
The signal handler also covers ordinary service/OS shutdown. It does not handle SIGKILL,
SIGABRT or power failure: those remain unclean exits. Watchdogs that need crash recovery must
classify their forced termination accordingly instead of issuing a normal graceful shutdown.

## Performance and limits

During play there are no world/physics enumerations, recursive entity snapshots, synchronous
file writes, or waits for the recovery writer. Discovery, child traversal and captures receive
alternating work slots, so continuous container events cannot starve durable snapshots.
Tree discovery is resumed one edge at a time. Parents with at most 64 children retain a bounded
snapshot of child IDs to survive containment changes between frames; larger trees retain an
incremental enumerator. A container's first capture waits for child identity registration.
Serializer delegates are prepared incrementally from item prototypes in the lobby before allowing
round start, avoiding their first-use compilation cost during a player's initial inventory capture.
Dirty work is deduplicated, captures have a global elapsed-time and entity-count budget, and
one background writer submits batches of at most 64 records. Time is measured with a monotonic
clock. The budget is cooperative: one component serializer or an entity startup callback cannot
be interrupted midway. It is not a hard realtime guarantee.

Minute summaries report tracked entities, queue size, writer failures and measured update times.
Failed captures back off for one minute and reschedule their checkpoint at that retry time,
rather than waiting for the regular 15-minute interval. Database retries back off up to one minute. A bad entity
keeps its previous successful data instead of blocking other characters. Incomplete snapshots
retain an outstanding record for investigation while other recoverable items can be delivered.
The registry is limited to 65,536 entities; serialized entity data is limited to 1 MiB.

Snapshots are asynchronous. The last uncommitted changes can be lost in an abrupt process/host
failure, and initial inventory coverage takes time to build. This feature is not an ACID save of
the entire game world, nor protection against losing the database/storage device. Live-server
latency at 70 network clients must be measured separately from the local 70-body capture benchmark.
The deterministic engine integration test uses its required synchronous in-memory database;
reported recovery update CPU excludes that test-only writer. Production always uses the background writer.

## Verification

`CrashRecoveryTests` exercises restart classification, writer leases, scope isolation, stale cash
snapshots, atomic purchase receipts/refunds, repeated claims and exact research amounts across
another crash. `CrashRecoveryIntegrationTests` exercises real engine serialization and nested
inventory/implant capture for 70 sessionless bodies, including paused maps. Control's
`test_crash_recovery.py` mocks all HTTP and service operations.

Before production enablement, test a real abrupt local process termination, apartment/parking
handoffs, implant actions and the 70-client workload using the same database engine as production.
