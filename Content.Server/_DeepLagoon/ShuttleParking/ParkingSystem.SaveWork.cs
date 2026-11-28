using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private const int MaximumSnapshotAttempts = 4;
    private const int SnapshotEntitiesPerStep = 64;
    private const double SnapshotStepBudgetMilliseconds = 2;
    private string _savePhase = "idle";
    private SaveWork? _saveWork;
    private long _saveFailures;

    private async Task WaitForSaveWriter()
    {
        // Do not let another console's synchronous SQLite mutation wait on a
        // background writer while holding the simulation thread.
        while ((_saveWork?.Finish ?? _saveWork?.Commit) is { IsCompleted: false } writer)
            try { await writer; } catch { /* the owning save reports its own error */ }
        while (_freeParkingWrite is { IsCompleted: false } freeWriter)
            try { await freeWriter; } catch { /* the owning capture reports its own error */ }
    }

    private sealed record SavePlan(StoredShuttle Stored, ParkingPayout? Payout, HashSet<EntityUid> Retained,
        HashSet<EntityUid> Excluded, HashSet<EntityUid> Confiscated, double ConfiscatedValue, double MaterialValue,
        double ResearchPrice, int UnsupportedResearch, string ResearchNotice);

    private sealed class SaveWork(Context context, Guid slot, EntityUid grid, IEnumerator<FilteredSnapshotResult?> steps)
    {
        public readonly Context Context = context;
        public readonly Guid Slot = slot;
        public readonly EntityUid Grid = grid;
        public readonly IEnumerator<FilteredSnapshotResult?> Steps = steps;
        public readonly TaskCompletionSource<string> Completion = new();
        public readonly Dictionary<string, (int Steps, double Total, double Max)> Timings = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public Task? Commit;
        public Task? Finish;
        public Task<SavePlan>? Receipt;
        public SavePlan? Plan;
        public string Message = "Шаттл сохранён.";
    }

    private void UpdateSaveWork()
    {
        if (_saveWork is not { } work) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (work.Finish is { } finish)
            {
                _savePhase = "finish_commit";
                if (!finish.IsCompleted) return;
                finish.GetAwaiter().GetResult();
                if (work.Plan!.Payout is { } payout && PaySale(payout))
                    work.Message = $"Шаттл сохранён. Зачислено {payout.Amount} кредитов." + work.Plan.ResearchNotice;
                CompleteSaveWork(work, null);
                return;
            }
            if (work.Commit is { } commit)
            {
                _savePhase = "durable_commit";
                if (!commit.IsCompleted) return;
                commit.GetAwaiter().GetResult();
                _savePhase = "remove_source";
                // A successful durable commit always precedes returning cargo/deleting the source.
                if (Exists(work.Grid)) work.Message = FinishSavedShip(work, work.Plan!);
                work.Finish = _store.FinishSaveAsync(work.Context.Session.UserId.UserId, work.Slot,
                    _timing.RealTime + Content.Shared._DeepLagoon.ShuttleParking.ParkingRules.FreeReturnDelay);
                return;
            }
            if (work.Receipt is { } receipt)
            {
                _savePhase = "economy_receipt";
                if (!receipt.IsCompleted) return;
                work.Plan = receipt.GetAwaiter().GetResult();
                work.Commit = _store.SaveCapturedAsync(work.Context.Session.UserId.UserId, work.Slot,
                    work.Plan.Stored, work.Plan.Payout);
                return;
            }
            if (TerminatingOrDeleted(work.Grid) || !Comp<ParkingShuttleComponent>(work.Grid).Parked)
                throw new InvalidOperationException("Исходный шаттл изменился или исчез; сохранение отменено.");
            if (!work.Steps.MoveNext()) throw new InvalidOperationException("Подготовка снимка не вернула результат.");
            if (work.Steps.Current is not { } snapshot) return;
            work.Steps.Dispose();
            _savePhase = "prepare_transaction";
            work.Plan = PrepareSavePlan(work, snapshot);
            if (work.Plan.Payout != null && EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().Enabled)
            {
                work.Receipt = PrepareSaveReceipt(work.Plan);
                _ = work.Receipt.ContinueWith(task => { _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            else work.Commit = _store.SaveCapturedAsync(work.Context.Session.UserId.UserId, work.Slot,
                work.Plan.Stored, work.Plan.Payout);
        }
        catch (Exception error) { CompleteSaveWork(work, error); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var timing = work.Timings.GetValueOrDefault(_savePhase);
            work.Timings[_savePhase] = (timing.Steps + 1, timing.Total + elapsed, Math.Max(timing.Max, elapsed));
        }
    }

    private async Task<SavePlan> PrepareSaveReceipt(SavePlan plan)
    {
        var payout = plan.Payout!;
        var account = await _database.ReadEconomyAccountAsync(payout.Owner, payout.Character);
        var materials = (long)Math.Clamp(Math.Floor(plan.MaterialValue), 0, payout.Amount);
        var research = (long)Math.Clamp(Math.Floor(plan.ResearchPrice), 0, payout.Amount - materials);
        return plan with { Payout = payout with { ProfileId = account.Profile, Costs = new()
        { ["Materials"] = materials, ["Recovery"] = research, ["Industry"] = payout.Amount - materials - research } } };
    }

    private void CompleteSaveWork(SaveWork work, Exception? error)
    {
        try { work.Steps.Dispose(); }
        finally { if (ReferenceEquals(_saveWork, work)) _saveWork = null; }
        var phases = string.Join("; ", work.Timings.Select(pair =>
            $"{pair.Key}: steps={pair.Value.Steps} total_ms={pair.Value.Total:F2} max_ms={pair.Value.Max:F2}"));
        Log.Info($"Parking save completed={error == null} elapsed_ms={work.Clock.Elapsed.TotalMilliseconds:F2}; {phases}");
        if (error == null) work.Completion.TrySetResult(work.Message);
        else
        {
            _saveFailures++;
            work.Completion.TrySetException(error);
        }
    }

    // Round-end/shutdown are explicit lifecycle boundaries. An in-flight durable
    // write must settle before a parked ship can be sold or its database disposed.
    // Normal gameplay only polls tasks and never waits for disk I/O.
    private void DrainSaveWork(bool publishWorld = true)
    {
        if (_saveWork is not { } work) return;
        if (work.Commit == null)
        {
            CompleteSaveWork(work, new OperationCanceledException("Сохранение отменено при завершении раунда."));
            return;
        }
        if (!publishWorld)
        {
            try { work.Commit.GetAwaiter().GetResult(); }
            catch (Exception error) { CompleteSaveWork(work, error); return; }
            if (work.Finish is { } finish) finish.GetAwaiter().GetResult();
            CompleteSaveWork(work, null);
            return;
        }
        while (ReferenceEquals(_saveWork, work))
        {
            // GetResult is only used here at a lifecycle boundary, never in Update.
            try { (work.Finish ?? work.Commit).GetAwaiter().GetResult(); }
            catch { /* UpdateSaveWork reports the failure and preserves the source. */ }
            UpdateSaveWork();
        }
    }
}
