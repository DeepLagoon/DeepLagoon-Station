using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.Performance;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.Diagnostics;

public sealed record PerformanceJournalReport(double At, string Line);
public sealed record PerformanceJournalHistory(PerformanceMonitorSample[] Samples, PerformanceJournalReport[] Reports, long Count, bool Truncated);

/// <summary>Independent round journal. Immutable snapshots only; all SQLite and JSON work runs on one background worker.</summary>
public sealed class PerformanceJournal : IAsyncDisposable
{
    private sealed record Entry(int Round, double At, PerformanceMonitorSample? Sample = null, string? Kind = null, string? Text = null,
        int Seconds = 0, TaskCompletionSource<PerformanceJournalHistory>? Read = null, TaskCompletionSource? Barrier = null,
        PerformanceMonitorPhase[]? Phases = null);
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly string? _directory;
    private readonly string _run = Guid.NewGuid().ToString("N");
    private long _dropped;
    private volatile string _fault = "";
    public string Status => _fault.Length > 0 ? "Журнал недоступен: " + _fault :
        (_directory == null ? "Журнал: RAM (нет каталога server-data)" : "Журнал: performance-monitor/round-<номер>.db")
        + $" · пропущено записей {Interlocked.Read(ref _dropped)}";

    public PerformanceJournal(string? directory)
    {
        _directory = directory;
        _worker = Task.Run(Run);
    }
    private bool Enqueue(Entry entry)
    {
        if (_queue.Writer.TryWrite(entry)) return true;
        Interlocked.Increment(ref _dropped); return false;
    }
    public bool Record(int round, PerformanceMonitorSample sample) => Enqueue(new(round, sample.Time, Sample: sample));
    public bool Phases(int round, double at, PerformanceMonitorPhase[] phases)
        => phases.Length == 0 || Enqueue(new(round, at, Phases: phases));
    public bool Report(int round, double at, string kind, string text)
        => text.Length <= 32768 && Enqueue(new(round, at, Kind: kind, Text: text));
    public Task<PerformanceJournalHistory> Read(int round, int seconds, double at)
    {
        var result = new TaskCompletionSource<PerformanceJournalHistory>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(round, at, Seconds: Math.Clamp(seconds, 0, 604800), Read: result)))
            result.SetException(new IOException("Journal queue busy"));
        return result.Task;
    }
    public async Task FlushAsync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(0, 0, Barrier: result));
        await result.Task;
    }
    public void Complete() => _queue.Writer.TryComplete();
    public async ValueTask DisposeAsync() { Complete(); await _worker; }

    private SqliteConnection Open(int round)
    {
        string path;
        if (_directory == null) path = ":memory:";
        else
        {
            Directory.CreateDirectory(_directory);
            path = Path.Combine(_directory, $"round-{Math.Max(0, round):D6}.db");
        }
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var schema = db.CreateCommand();
        schema.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=1000;
            CREATE TABLE IF NOT EXISTS samples(id INTEGER PRIMARY KEY,utc REAL NOT NULL,run TEXT NOT NULL,tick REAL NOT NULL,gc REAL NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS sample_time ON samples(utc);
            CREATE TABLE IF NOT EXISTS reports(id INTEGER PRIMARY KEY,utc REAL NOT NULL,run TEXT NOT NULL,kind TEXT NOT NULL,line TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS report_time ON reports(utc);
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT OR IGNORE INTO metadata VALUES('schema','1');
            INSERT OR IGNORE INTO metadata VALUES('time','utc seconds since unix epoch');
            """;
        schema.ExecuteNonQuery();
        return db;
    }

    private async Task Run()
    {
        SqliteConnection? db = null;
        var round = -1;
        var writes = 0;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    if (item.Barrier != null) { item.Barrier.TrySetResult(); continue; }
                    if (round != item.Round) { db?.Dispose(); db = Open(item.Round); round = item.Round; }
                    if (item.Read != null) { item.Read.TrySetResult(ReadHistory(db!, item.Seconds, item.At)); continue; }
                    using var command = db!.CreateCommand();
                    if (item.Sample is { } sample)
                    {
                        command.CommandText = "INSERT INTO samples(utc,run,tick,gc,payload) VALUES($at,$run,$tick,$gc,$data)";
                        command.Parameters.AddWithValue("$tick", sample.TickMs ?? -1);
                        command.Parameters.AddWithValue("$gc", sample.GcPauseMs);
                        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(sample));
                    }
                    else
                    {
                        command.CommandText = "INSERT INTO reports(utc,run,kind,line) VALUES($at,$run,$kind,$line)";
                        command.Parameters.AddWithValue("$kind", item.Phases == null ? item.Kind! : "phases");
                        command.Parameters.AddWithValue("$line", item.Phases == null ? item.Text! : "Monitor phases: " + JsonSerializer.Serialize(item.Phases));
                    }
                    command.Parameters.AddWithValue("$at", item.At);
                    command.Parameters.AddWithValue("$run", _run);
                    command.ExecuteNonQuery();
                    _fault = "";
                    if (++writes % 1000 == 0) Maintain(db);
                }
                catch (Exception error)
                {
                    _fault = error.GetType().Name;
                    Interlocked.Increment(ref _dropped);
                    item.Read?.TrySetException(error); item.Barrier?.TrySetException(error);
                    db?.Dispose(); db = null; round = -1;
                }
            }
        }
        finally { db?.Dispose(); }
    }

    private static void Maintain(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        // Hard bounded per-round retention. Oldest records are removed only after the generous week-long capacity.
        command.CommandText = """
            INSERT OR REPLACE INTO metadata SELECT 'truncated_samples','1'
              WHERE EXISTS(SELECT 1 FROM samples WHERE id < (SELECT COALESCE(MAX(id),0)-200000 FROM samples));
            INSERT OR REPLACE INTO metadata SELECT 'truncated_reports','1'
              WHERE EXISTS(SELECT 1 FROM reports WHERE id < (SELECT COALESCE(MAX(id),0)-500000 FROM reports));
            DELETE FROM samples WHERE id < (SELECT COALESCE(MAX(id),0)-200000 FROM samples);
            DELETE FROM reports WHERE id < (SELECT COALESCE(MAX(id),0)-500000 FROM reports);
            PRAGMA wal_checkpoint(PASSIVE);
            """;
        command.ExecuteNonQuery();
    }

    private static PerformanceJournalHistory ReadHistory(SqliteConnection db, int seconds, double at)
    {
        using var bounds = db.CreateCommand();
        bounds.CommandText = "SELECT COALESCE(MIN(utc),0),COALESCE(MAX(utc),0),COUNT(*) FROM samples";
        double first, last; long count;
        using (var reader = bounds.ExecuteReader()) { reader.Read(); first = reader.GetDouble(0); last = reader.GetDouble(1); count = reader.GetInt64(2); }
        var end = at > 0 ? Math.Min(at, last) : last;
        var start = seconds == 0 ? first : Math.Max(first, end - seconds);
        var bucket = Math.Max(1, (end - start) / 360d);
        using var query = db.CreateCommand();
        // Preserve the actual last observation and both CPU/GC peaks in each bucket; never manufacture averages or interpolate gaps.
        query.CommandText = """
            WITH points AS (SELECT *,CAST((utc-$start)/$bucket AS INTEGER) AS b FROM samples WHERE utc BETWEEN $start AND $end),
            selected AS (SELECT MAX(id) AS id FROM points GROUP BY b
              UNION SELECT id FROM (SELECT id,ROW_NUMBER() OVER(PARTITION BY b ORDER BY tick DESC,id DESC) AS n FROM points) WHERE n=1
              UNION SELECT id FROM (SELECT id,ROW_NUMBER() OVER(PARTITION BY b ORDER BY gc DESC,id DESC) AS n FROM points) WHERE n=1)
            SELECT payload FROM samples WHERE id IN (SELECT id FROM selected) ORDER BY utc,id LIMIT 1100;
            """;
        query.Parameters.AddWithValue("$start", start); query.Parameters.AddWithValue("$end", end); query.Parameters.AddWithValue("$bucket", bucket);
        var samples = new List<PerformanceMonitorSample>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
            if (JsonSerializer.Deserialize<PerformanceMonitorSample>(reader.GetString(0)) is { } sample) samples.Add(sample);
        using var reports = db.CreateCommand();
        reports.CommandText = "SELECT utc,line FROM (SELECT id,utc,line FROM reports WHERE utc<=$end ORDER BY utc DESC,id DESC LIMIT 512) ORDER BY utc,id";
        reports.Parameters.AddWithValue("$end", end);
        var lines = new List<PerformanceJournalReport>();
        using (var reader = reports.ExecuteReader()) while (reader.Read()) lines.Add(new(reader.GetDouble(0), reader.GetString(1)));
        using var retention = db.CreateCommand();
        retention.CommandText = "SELECT EXISTS(SELECT 1 FROM metadata WHERE key IN ('truncated_samples','truncated_reports') AND value='1')";
        return new(samples.ToArray(), lines.ToArray(), count, Convert.ToInt64(retention.ExecuteScalar()) != 0);
    }
}
