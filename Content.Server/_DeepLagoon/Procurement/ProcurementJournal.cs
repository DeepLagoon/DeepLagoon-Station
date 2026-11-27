using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Procurement;

/// <summary>
/// A bounded, single-reader audit worker. SQLite, JSON and counterfactual calculations run
/// outside the game thread. Dropped observations/failures are exposed, never called complete data.
/// This is an observational journal, not the authority for real payments.
/// </summary>
public sealed class ProcurementJournal : IAsyncDisposable
{
    private sealed record Work(ProcurementObservation? Observation, TaskCompletionSource? Barrier = null);
    public sealed class Checkpoint
    {
        public Dictionary<string, ProcurementBudget.State> Policies { get; set; } = new();
    }

    private readonly Channel<Work> _queue;
    private readonly Task _worker;
    private long _dropped;
    private long _written;
    private long _sales;
    private string _status = "starting";
    private string? _fault;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Written => Interlocked.Read(ref _written);
    public long Sales => Interlocked.Read(ref _sales);
    public string Status => Volatile.Read(ref _status);
    public string? Fault => Volatile.Read(ref _fault);

    public ProcurementJournal(string path, long startedAt, int capacity = 2048)
    {
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(() => Run(path, startedAt));
    }

    public bool TryRecord(ProcurementObservation observation)
    {
        if (_queue.Writer.TryWrite(new(observation)))
            return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(null, barrier)).ConfigureAwait(false);
        await barrier.Task.ConfigureAwait(false);
        if (Fault != null)
            throw new IOException("Procurement observation worker failed: " + Fault);
    }

    public void Complete() => _queue.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        Complete();
        await _worker.ConfigureAwait(false);
    }

    private async Task Run(string path, long startedAt)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using (var schema = db.CreateCommand())
            {
                schema.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=1000;
                    CREATE TABLE IF NOT EXISTS observations(
                        kind TEXT NOT NULL,id TEXT NOT NULL,utc_ms INTEGER NOT NULL,round INTEGER NOT NULL,
                        parent TEXT,payload TEXT NOT NULL,decisions TEXT NOT NULL,dropped INTEGER NOT NULL,
                        PRIMARY KEY(kind,id));
                    CREATE INDEX IF NOT EXISTS procurement_parent ON observations(parent,kind);
                    CREATE TABLE IF NOT EXISTS shadow_state(id INTEGER PRIMARY KEY CHECK(id=1),payload TEXT NOT NULL);
                    """;
                schema.ExecuteNonQuery();
            }
            Checkpoint checkpoint;
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT payload FROM shadow_state WHERE id=1";
                checkpoint = read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Checkpoint>(json)! : new();
            }
            var models = ProcurementPolicy.Candidates.Select(p => new ProcurementBudget(p, checkpoint.Policies.GetValueOrDefault(p.Id))).ToArray();
            // Do not invent player-hours while the process was stopped or observation disabled.
            long lastAccrual = startedAt, lastSample = startedAt;
            var population = 0;
            Write(new(Guid.NewGuid(), startedAt, 0, "observation_started", Reason: "offline_time_not_accrued"));
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                if (work.Barrier != null)
                {
                    work.Barrier.TrySetResult();
                    continue;
                }
                Write(work.Observation!);
            }

            void Write(ProcurementObservation observation)
            {
                using var tx = db.BeginTransaction();
                using (var exists = db.CreateCommand())
                {
                    exists.Transaction = tx;
                    exists.CommandText = "SELECT 1 FROM observations WHERE kind=$kind AND id=$id";
                    exists.Parameters.AddWithValue("$kind", observation.Kind);
                    exists.Parameters.AddWithValue("$id", observation.Id.ToString());
                    if (exists.ExecuteScalar() != null)
                        return; // Stable parking payout IDs and retried observations cannot spend twice.
                }
                var now = observation.UtcMilliseconds;
                var elapsed = now - lastAccrual;
                var gap = now - lastSample > 60000 || now < lastAccrual;
                if (gap)
                    observation = observation with { Reason = (observation.Reason ?? "") + ";observation_gap_no_accrual" };
                if (!gap && elapsed > 0)
                    foreach (var model in models)
                        model.Accrue(elapsed / 3600000m, population, now);
                if (now >= lastAccrual)
                    lastAccrual = now;
                if (observation.Kind == "population")
                {
                    population = observation.ActivePlayers;
                    lastSample = now;
                }
                var decisions = new List<ProcurementShadowDecision>();
                if (observation.Sale is { } sale)
                {
                    var (cost, category) = ProcurementBudget.Cost(sale);
                    using var rewards = db.CreateCommand();
                    rewards.Transaction = tx;
                    rewards.CommandText = "SELECT payload FROM observations WHERE kind='bounty_reward' AND parent=$id";
                    rewards.Parameters.AddWithValue("$id", sale.Id.ToString());
                    using (var reader = rewards.ExecuteReader())
                        while (reader.Read())
                        {
                            var reward = JsonSerializer.Deserialize<ProcurementObservation>(reader.GetString(0))!;
                            if (reward.Currency == "Credit")
                                cost += reward.Amount;
                        }
                    foreach (var model in models)
                        decisions.Add(model.Observe(now, sale.Player, category, cost, sale.ManifestComplete));
                }
                else if (observation.Kind == "parking_payment")
                {
                    using var intent = db.CreateCommand();
                    intent.Transaction = tx;
                    intent.CommandText = "SELECT payload FROM observations WHERE kind='parking_manifest' AND id=$id";
                    intent.Parameters.AddWithValue("$id", observation.Id.ToString());
                    var manifest = intent.ExecuteScalar() is string json
                        ? JsonSerializer.Deserialize<ProcurementObservation>(json)?.Parking : null;
                    var category = manifest is { ConfiscatedAppraisal: 0, ResearchAppraisal: 0 }
                        ? ProcurementCategory.Materials
                        : manifest is { ConfiscatedAppraisal: 0, MaterialAppraisal: 0 }
                            ? ProcurementCategory.Recovery : ProcurementCategory.Unclassified;
                    foreach (var model in models)
                        decisions.Add(model.Observe(now, observation.Player, category, observation.Amount, manifest?.ManifestComplete == true));
                }
                using (var insert = db.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO observations VALUES($kind,$id,$now,$round,$parent,$payload,$decisions,$dropped)";
                    insert.Parameters.AddWithValue("$kind", observation.Kind);
                    insert.Parameters.AddWithValue("$id", observation.Id.ToString());
                    insert.Parameters.AddWithValue("$now", now);
                    insert.Parameters.AddWithValue("$round", observation.Round);
                    insert.Parameters.AddWithValue("$parent", (object?) observation.Parent?.ToString() ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(observation));
                    insert.Parameters.AddWithValue("$decisions", JsonSerializer.Serialize(decisions));
                    insert.Parameters.AddWithValue("$dropped", Dropped);
                    insert.ExecuteNonQuery();
                }
                checkpoint.Policies = models.ToDictionary(m => m.Policy.Id, m => m.Data);
                using (var save = db.CreateCommand())
                {
                    save.Transaction = tx;
                    save.CommandText = "INSERT INTO shadow_state VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                    save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(checkpoint));
                    save.ExecuteNonQuery();
                }
                tx.Commit();
                Interlocked.Increment(ref _written);
                if (observation.Kind == "pallet_sale")
                    Interlocked.Increment(ref _sales);
                Volatile.Write(ref _status, JsonSerializer.Serialize(new
                {
                    At = now, Gap = gap, Active = population, Dropped,
                    Policies = models.Select(m => new { m.Policy.Id, m.Data.Issued, m.Data.Spent, m.Data.Expired, m.Data.Accepted, m.Data.Rejected }),
                }));
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _fault, e.GetType().Name);
            _queue.Writer.TryComplete(e);
            while (_queue.Reader.TryRead(out var remaining))
                remaining.Barrier?.TrySetException(e);
        }
    }
}
