# Round performance journal

The load monitor records measurements independently of whether its window is open.
Each actual game round has a separate database under the server data directory:

```
performance-monitor/round-000213.db
```

`performance_monitor.journal_enabled` defaults to `true`; changing it at runtime
stops/resumes disk recording. `performance_monitor.sample_interval` defaults to
5 seconds and is clamped to 1–60 seconds. These values can be placed in the
`[performance_monitor]` section of the server configuration. The RAM ring retains
720 recent points. Logging never automatically enables engine diagnostic levels,
traces, heap snapshots or forced garbage collection.

Disk I/O, JSON serialization, history queries and downsampling run on a bounded
background queue (256 entries). A slow or unavailable disk cannot block a tick;
the monitor displays dropped entry counts and storage errors. SQLite uses WAL and
NORMAL synchronous mode. A process crash preserves committed entries; an OS or
power failure can lose the most recent WAL commits. Keep the database and its
`-wal`/`-shm` files together when copying it while the server is running.

The schema is intentionally usable without the game:

| Table | Columns | Meaning |
| --- | --- | --- |
| `samples` | `id`, `utc`, `run`, `tick`, `gc`, `payload` | Actual base measurement and JSON of `PerformanceMonitorSample` |
| `reports` | `id`, `utc`, `run`, `kind`, `line` | Original engine summary, slow-tick/PVS warning, system/phase/component report, or monitor phase sample |
| `metadata` | `key`, `value` | Schema version and timestamp units |

`utc` is Unix seconds in UTC, `run` identifies the recording process. The base
sample records the real sampling window, memory, allocation rate, GC pause,
entity count and available phase times. Missing/stale measurements remain null,
not measured zero. Engine reports retain their original numerical units and
coverage flags. PVS handler timings are sampled actual calls, not extrapolated
totals or percentages of CPU.

The journal keeps up to 200,000 samples and 500,000 raw report records per round.
At the default 5-second base interval, the sample limit covers over 11 days.
Extremely verbose diagnostics can reach the report quota sooner; oldest records
are pruned. Completed rounds are not erased by a normal round restart. Old round
files can be archived or removed by the server operator.

## Offline export

Only Python's standard library is required. Export remains read-only, streams
rows in chronological order and works with the game server shut down:

```powershell
python Tools/export_performance_journal.py server-data/performance-monitor/round-000213.db --output round-213.jsonl
python Tools/export_performance_journal.py server-data/performance-monitor/round-000213.db --from 2026-11-26T18:00:00Z --to 2026-11-26T19:00:00Z --kind reports --output peak-hour.jsonl
```

The UI loads its history from this database, offers 30 seconds through 7 days and
the whole round, and preserves the last observation plus measured tick/GC peaks
in each display bucket. A graph click opens measurements and the latest reports
at that moment; “В эфир” returns to current measurements. Historical entity IDs
are never offered as cleanup targets. Diagnostic control buttons always affect
the current server, not the historical snapshot.

Read `SummaryWindowSeconds`/report `window_s` and report age before associating a
peak with a system. Rankings describe an observation window; nested phase times
overlap. A GC pause or large entity count at the same moment is a correlation,
not proof of the cause. Slow-tick records and separate maximum call/tick,
allocation, failure, PVS coverage, queue and worker measurements narrow down the
actual work to inspect.
