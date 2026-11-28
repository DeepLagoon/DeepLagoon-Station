using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Performance;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private readonly Queue<FreeParkingSnapshot> _freeSaleQueue = new();
    private readonly HashSet<(Guid Identity, Guid Revision)> _freeSaleQueued = new();
    private Task<FreeParkingSnapshot[]>? _freeSaleIndex;
    private bool _freeSaleScanRequested = true;
    private TimeSpan _freeSaleRetry;
    private RecoveredParkingSale? _freeSale;

    private sealed class RecoveredParkingSale(FreeParkingSnapshot snapshot, Task<FreeParkingRead> read)
    {
        public readonly FreeParkingSnapshot Snapshot = snapshot;
        public readonly Task<FreeParkingRead> Read = read;
        public EntityUid? Map;
        public MapLoaderSystem.IncrementalGridLoad? Load;
        public IEnumerator<bool>? Appraisal;
        public int? Price;
        public Task? Commit;
    }

    public bool IsParkingValuationGrid(EntityUid grid) => _freeSale is { } sale &&
        (sale.Load?.Entities.Contains(grid) == true || sale.Map is { } map && Transform(grid).MapUid == map);

    private void RequestRecoveredParkingSaleScan() => _freeSaleScanRequested = true;

    /// <summary>
    /// Shares the free-parking ECS step budget: the caller runs no capture or recall alongside this work.
    /// File reads, parsing and the durable sale/payout transaction run off the simulation thread.
    /// </summary>
    private bool UpdateRecoveredParkingSales()
    {
        if (_freeSaleScanRequested && _freeSaleIndex == null && _timing.RealTime >= _freeSaleRetry)
        {
            _freeSaleScanRequested = false;
            _freeSaleIndex = _store.PendingFreeParkingSalesAsync();
        }
        if (_freeSaleIndex is { IsCompleted: true } index)
        {
            _freeSaleIndex = null;
            try
            {
                foreach (var snapshot in index.GetAwaiter().GetResult())
                    if (_freeSaleQueued.Add((snapshot.Identity, snapshot.Revision))) _freeSaleQueue.Enqueue(snapshot);
            }
            catch (Exception error)
            {
                RetryRecoveredParkingSales();
                Log.Error($"Recovered parking sale index unavailable: {error}");
            }
        }
        if (_freeSale == null && _timing.RealTime >= _freeSaleRetry && _freeSaleQueue.TryDequeue(out var next))
            _freeSale = new RecoveredParkingSale(next, _store.ReadFreeParkingAsync(next));
        if (_freeSale is not { } work) return false;
        try
        {
            if (work.Commit is { } commit)
            {
                if (!commit.IsCompleted) return true;
                commit.GetAwaiter().GetResult();
                Log.Info($"Sold recovered parked shuttle {work.Snapshot.Identity} for {work.Price} credits to {work.Snapshot.Owner} profile {work.Snapshot.Profile}.");
                _freeParkingRecovered.Remove(work.Snapshot.Identity);
                _nextPayoutRetry = TimeSpan.Zero;
                FinishRecoveredParkingSale(work);
                return true;
            }
            if (work.Load == null)
            {
                if (!work.Read.IsCompleted) return true;
                var read = work.Read.GetAwaiter().GetResult();
                if (work.Snapshot.Profile <= 0) throw new InvalidDataException("Recovered sale has no immutable owner profile.");
                work.Map = _map.CreateMap(out var mapId, runMapInit: false);
                _map.SetPaused(mapId, true);
                EnsureComp<ParkingStagingSnapshotComponent>(work.Map.Value);
                work.Load = _loader.BeginIncrementalGridLoad(mapId, () => new StringReader(read.Grid),
                    new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
            }
            var start = Stopwatch.GetTimestamp();
            for (var step = 0; step < FreeParkingStepsPerTick; step++)
            {
                if (work.Appraisal != null)
                {
                    if (!work.Appraisal.MoveNext())
                    {
                        if (work.Price is not { } price) throw new InvalidDataException("Recovered sale appraisal returned no price.");
                        var snapshot = work.Snapshot;
                        var payout = new ParkingPayout(snapshot.Identity, snapshot.Owner, snapshot.Character, price,
                            snapshot.Profile, new Dictionary<string, long> { ["Industry"] = price });
                        work.Commit = _store.CommitFreeParkingSaleAsync(snapshot, payout);
                        _freeParkingWrite = work.Commit;
                        return true;
                    }
                }
                else if (!work.Load.MoveNext())
                {
                    if (work.Load.Grid is not { } grid || work.Load.RequiresSynchronousLoad)
                        throw new InvalidDataException("Recovered sale requires a complete incremental grid load.");
                    EnsureComp<ParkingStagingSnapshotComponent>(grid.Owner);
                    // This grid remains paused and private throughout appraisal. In particular, do not bind
                    // a deed, register a station or reconnect devices just to dispose of it after the sale.
                    work.Appraisal = _shipyard.AppraiseParkedShuttleSteps(grid.Owner, price => work.Price = price).GetEnumerator();
                }
                if (Stopwatch.GetElapsedTime(start) >= FreeParkingBudget) break;
            }
        }
        catch (Exception error)
        {
            Log.Error($"Recovered parking sale {work.Snapshot.Identity}/{work.Snapshot.Revision} is pending: {error}");
            FinishRecoveredParkingSale(work);
            RetryRecoveredParkingSales();
        }
        return true;
    }

    private void RetryRecoveredParkingSales()
    {
        _freeSaleRetry = _timing.RealTime + TimeSpan.FromSeconds(30);
        _freeSaleScanRequested = true;
    }

    private void FinishRecoveredParkingSale(RecoveredParkingSale work)
    {
        work.Appraisal?.Dispose();
        work.Load?.Dispose();
        // Some entities have not been parented yet when a cooperative load is cancelled. Delete the
        // complete allocation set as well as the staging map, using the same bounded garbage queue.
        if (work.Load != null) _freeParkingGarbage.Enqueue(work.Load.Entities.GetEnumerator());
        if (work.Map is { } map) EntityManager.System<StagedMapDeletionSystem>().Queue(map);
        _freeSaleQueued.Remove((work.Snapshot.Identity, work.Snapshot.Revision));
        if (_freeSale == work) _freeSale = null;
    }

    private void StopRecoveredParkingSales()
    {
        if (_freeSale is { } work)
        {
            ObserveAbandonedSaleTask(work.Read);
            if (work.Commit != null) ObserveAbandonedSaleTask(work.Commit);
            FinishRecoveredParkingSale(work);
        }
        if (_freeSaleIndex != null) ObserveAbandonedSaleTask(_freeSaleIndex);
        _freeSaleIndex = null;
        _freeSaleQueue.Clear();
        _freeSaleQueued.Clear();
        _freeSaleScanRequested = true;
        _freeSaleRetry = TimeSpan.Zero;
    }

    private static void ObserveAbandonedSaleTask(Task task)
    {
        _ = task.ContinueWith(failed => { _ = failed.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
