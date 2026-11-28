using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.GameTicking;
using Content.Server._DeepLagoon.Performance;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Server._NF.Bank;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private const int FreeParkingStepsPerTick = 16;
    private static readonly TimeSpan FreeParkingBudget = TimeSpan.FromMilliseconds(.5);
    private readonly LinkedList<FreeParkingCapture> _freeParkingQueue = new();
    private readonly Dictionary<EntityUid, FreeParkingCapture> _freeParkingCaptures = new();
    private readonly Dictionary<Guid, FreeParkingSnapshot> _freeParkingRecovered = new();
    private Task<FreeParkingSnapshot[]>? _freeParkingIndex;
    private FreeParkingCapture? _freeParkingCapture;
    private Task? _freeParkingWrite;
    private readonly SemaphoreSlim _freeParkingRecallGate = new(1, 1);
    private FreeParkingRecall? _freeParkingRecall;
    private readonly Queue<IEnumerator<EntityUid>> _freeParkingGarbage = new();
    private readonly Queue<(EntityUid Console, EntityUid Actor)> _freeParkingDirty = new();
    private readonly HashSet<(EntityUid Console, EntityUid Actor)> _freeParkingDirtySet = new();
    private uint _freeParkingGeneration;
    private uint _freeParkingPublicationTick = uint.MaxValue;
    private TaskCompletionSource? _freeParkingPublicationTurn;

    private sealed class FreeParkingCapture(EntityUid grid, FreeParkingSnapshot snapshot)
    {
        public readonly EntityUid Grid = grid;
        public FreeParkingSnapshot Snapshot = snapshot;
        public Task<int?>? Profile;
        public IEnumerator<SnapshotWrite?>? Steps;
        public Task? Begin;
        public Task<bool>? Commit;
        public LinkedListNode<FreeParkingCapture>? QueueNode;
        public bool Cancelled, Saved;
        public string Error = "";
        public int Advances;
        public double MaximumAdvanceMs;
    }

    private sealed class FreeParkingRecall(Context context, FreeParkingSnapshot snapshot, EntityUid map,
        MapLoaderSystem.IncrementalGridLoad load)
    {
        public readonly Context Context = context;
        public readonly FreeParkingSnapshot Snapshot = snapshot;
        public readonly EntityUid Map = map;
        public readonly MapLoaderSystem.IncrementalGridLoad Load = load;
        public IEnumerator<bool>? Equipment;
        public readonly TaskCompletionSource<EntityUid> Completion = new();
    }

    private void InitializeFreeParkingRecovery() => _freeParkingIndex = _store.FreeParkingIndexAsync();

    private void QueueFreeParkingSnapshot(EntityUid grid)
    {
        if (!TryComp<ParkingShuttleComponent>(grid, out var ship) || !ship.Parked || ship.ParkingCharacter < 0 ||
            _freeParkingCaptures.ContainsKey(grid) || IsVoucherPurchasedShuttle(grid)) return;
        var snapshot = new FreeParkingSnapshot(ship.Identity, ship.OwnerAccount, ship.ParkingCharacter,
            // Free recovery never consumes a paid berth; category is optional display metadata.
            Guid.NewGuid(), Name(grid), ship.OwnerName, -1, ship.OriginalPrice, "", ship.ParkingProfile, ship.OriginFaction, ship.Vessel);
        var capture = new FreeParkingCapture(grid, snapshot);
        _freeParkingCaptures.Add(grid, capture);
        capture.QueueNode = _freeParkingQueue.AddLast(capture);
    }

    private void UpdateFreeParking()
    {
        if (_freeParkingPublicationTurn is { } turn && _freeParkingPublicationTick != _timing.CurTick.Value)
        { _freeParkingPublicationTurn = null; turn.TrySetResult(); }
        if (_freeParkingDirty.TryDequeue(out var dirty))
        {
            _freeParkingDirtySet.Remove(dirty);
            _ = Publish(dirty.Console, dirty.Actor);
        }
        if (UpdateFreeParkingGarbage()) return;
        if (_freeParkingIndex is { IsCompleted: true } index)
        {
            _freeParkingIndex = null;
            try { foreach (var snapshot in index.GetAwaiter().GetResult()) _freeParkingRecovered.TryAdd(snapshot.Identity, snapshot); }
            catch (Exception error) { Log.Error($"Free parking recovery index unavailable: {error}"); }
        }
        if (_freeParkingRecall != null) { UpdateFreeParkingRecall(); return; }
        if (UpdateRecoveredParkingSales()) return;
        if (_saveWork != null || _ticker.RunLevel == GameRunLevel.PostRound) return;
        if (_freeParkingCapture == null)
        {
            while (_freeParkingQueue.First is { } first)
            {
                var next = first.Value; _freeParkingQueue.RemoveFirst(); next.QueueNode = null;
                if (next.Cancelled) continue;
                _freeParkingCapture = next;
                break;
            }
        }
        if (_freeParkingCapture is not { } work) return;
        try
        {
            if (work.Cancelled || TerminatingOrDeleted(work.Grid) || !Comp<ParkingShuttleComponent>(work.Grid).Parked)
            { FinishFreeParkingCapture(work); return; }
            if (work.Snapshot.Profile <= 0)
            {
                work.Profile ??= _database.GetRecoveryProfile(work.Snapshot.Owner, work.Snapshot.Character);
                if (!work.Profile.IsCompleted) return;
                var profile = work.Profile.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Персонаж владельца больше не существует.");
                work.Snapshot = work.Snapshot with { Profile = profile };
                Comp<ParkingShuttleComponent>(work.Grid).ParkingProfile = profile;
                RememberFreeParkingCard(work.Grid);
            }
            if (work.Begin == null && work.Steps == null && work.Commit == null)
            {
                work.Begin = _store.BeginFreeParkingAsync(work.Snapshot); _freeParkingWrite = work.Begin; return;
            }
            if (work.Begin is { } begin)
            {
                if (!begin.IsCompleted) return;
                begin.GetAwaiter().GetResult(); work.Begin = null;
                work.Steps = SerializeSnapshotSteps(work.Grid, recover: false, backgroundEncode: true, idleParking: true).GetEnumerator();
            }
            if (work.Commit is { } commit)
            {
                if (!commit.IsCompleted) return;
                work.Saved = commit.GetAwaiter().GetResult();
                if (!work.Saved) work.Error = "Ошибка";
                if (work.Saved) _freeParkingRecovered[work.Snapshot.Identity] = work.Snapshot;
                FinishFreeParkingCapture(work); return;
            }
            var start = Stopwatch.GetTimestamp();
            for (var step = 0; step < FreeParkingStepsPerTick; step++)
            {
                var advance = Stopwatch.GetTimestamp();
                var moved = work.Steps!.MoveNext();
                work.Advances++; work.MaximumAdvanceMs = Math.Max(work.MaximumAdvanceMs, Stopwatch.GetElapsedTime(advance).TotalMilliseconds);
                if (!moved) throw new InvalidDataException("Free-parking capture returned no snapshot.");
                if (work.Steps.Current is { } result)
                {
                    work.Steps.Dispose(); work.Steps = null;
                    work.Commit = _store.CommitFreeParkingAsync(work.Snapshot, result.Data, result.RecoveryAssets ?? []);
                    _freeParkingWrite = work.Commit;
                    return;
                }
                if (Stopwatch.GetElapsedTime(start) >= FreeParkingBudget) break;
            }
        }
        catch (Exception error)
        {
            work.Error = "Ошибка";
            Log.Error($"Free parking capture {work.Snapshot.Identity}/{work.Snapshot.Revision} failed: {error}");
            FinishFreeParkingCapture(work);
        }
    }

    private void FinishFreeParkingCapture(FreeParkingCapture work)
    {
        work.Steps?.Dispose(); work.Steps = null;
        if (_freeParkingCapture == work) _freeParkingCapture = null;
        if (!work.Cancelled) Log.Info($"Free parking capture {work.Snapshot.Identity}: saved={work.Saved}; state={work.Error}; advances={work.Advances}; max_advance_ms={work.MaximumAdvanceMs:F3}");
        foreach (var (console, actors) in _viewers)
            foreach (var actor in actors)
                if (TryComp<ActorComponent>(actor, out var owner) && owner.PlayerSession.UserId.UserId == work.Snapshot.Owner &&
                    _freeParkingDirtySet.Add((console, actor))) _freeParkingDirty.Enqueue((console, actor));
    }

    private string FreeParkingStatus(EntityUid grid) => _freeParkingCaptures.TryGetValue(grid, out var work)
        ? work.Error.Length > 0 ? work.Error : work.Saved ? "Сохранён" : "Сохранение"
        : "Сохранение";

    private async Task CancelFreeParkingSnapshot(EntityUid grid)
    {
        if (_freeParkingCaptures.Remove(grid, out var work))
        {
            work.Cancelled = true;
            if (work.QueueNode is { } node) { _freeParkingQueue.Remove(node); work.QueueNode = null; }
            if (_freeParkingCapture == work) FinishFreeParkingCapture(work);
            else { work.Steps?.Dispose(); work.Steps = null; }
            var suspend = _store.SuspendFreeParkingAsync(work.Snapshot.Identity, work.Snapshot.Revision);
            _freeParkingWrite = suspend;
            if (await suspend is { } previous) _freeParkingRecovered[previous.Identity] = previous;
        }
        else if (TryComp<ParkingShuttleComponent>(grid, out var ship) && _freeParkingRecovered.TryGetValue(ship.Identity, out var snapshot))
        {
            var suspend = _store.SuspendFreeParkingAsync(snapshot.Identity, snapshot.Revision);
            _freeParkingWrite = suspend; await suspend;
        }
    }

    private FreeParkingSnapshot? RecoverableParking(Context context)
    {
        if (!TryComp<BankCharacterComponent>(context.Actor, out var character)) return null;
        var owned = _freeParkingRecovered.Values.Where(snapshot => snapshot.Owner == context.Session.UserId.UserId &&
            snapshot.Character == context.Character && ParkingOriginMatches(snapshot.Owner, snapshot.Profile, snapshot.OriginFaction,
                character.User, character.Profile, GetParkingOriginFaction(context.Actor)) && !_activeShips.ContainsKey(snapshot.Identity));
        if (TryComp<ParkingDeedIdentityComponent>(context.Card, out var deed) && deed.Owner == character.User && deed.Profile == character.Profile)
            return owned.FirstOrDefault(snapshot => snapshot.Identity == deed.Identity);
        return owned.FirstOrDefault();
    }

    private void RememberFreeParkingCard(EntityUid grid, EntityUid? card = null)
    {
        var ship = Comp<ParkingShuttleComponent>(grid);
        card ??= CompOrNull<ShuttleDeedComponent>(grid)?.DeedHolder;
        if (card is not { } identity || TerminatingOrDeleted(identity)) return;
        var deed = EnsureComp<ParkingDeedIdentityComponent>(identity);
        deed.Identity = ship.Identity; deed.Owner = ship.OwnerAccount; deed.Profile = ship.ParkingProfile;
    }

    private async Task RecallParked(Context context, Robust.Shared.GameObjects.NetEntity? dock)
    {
        var generation = _freeParkingGeneration;
        await _freeParkingRecallGate.WaitAsync();
        try
        {
            if (generation != _freeParkingGeneration) throw new OperationCanceledException("Parking lifecycle ended.");
            await RecallParkedCore(context, dock, generation);
        }
        finally { _freeParkingRecallGate.Release(); }
    }

    private async Task RecallParkedCore(Context context, Robust.Shared.GameObjects.NetEntity? dock, uint generation)
    {
        if (OwnedShip(context) is { } live)
        {
            if (!Comp<ParkingShuttleComponent>(live).Parked) throw new InvalidOperationException("Шаттл не находится на парковке.");
            CheckVoucherParkingAllowed(live); ValidateParkingOrigin(context.Actor, Comp<ParkingShuttleComponent>(live));
            await CancelFreeParkingSnapshot(live);
            var snapshot = _freeParkingRecovered.GetValueOrDefault(Comp<ParkingShuttleComponent>(live).Identity);
            CheckParkingFleetLimit(Comp<ParkingShuttleComponent>(live).Vessel, Comp<ParkingShuttleComponent>(live).Identity);
            var claimed = snapshot != null && await _store.ClaimFreeParkingAsync(snapshot);
            if (snapshot != null && !claimed) throw new InvalidOperationException("Снимок парковки изменился.");
            try
            {
                await WaitFreeParkingPublicationTurn();
                if (generation != _freeParkingGeneration || GetContext(context.Console, context.Actor)?.Card != context.Card) throw new InvalidOperationException("Доступ к парковке изменился.");
                ValidateParkingOrigin(context.Actor, Comp<ParkingShuttleComponent>(live));
                Deliver(context, live, dock);
                if (snapshot != null) CompleteFreeParkingPublication(snapshot);
            }
            catch
            {
                RehideFreeParkingGrid(live);
                if (claimed) await _store.AbortFreeParkingClaimAsync(snapshot!);
                QueueFreeParkingSnapshot(live); throw;
            }
            return;
        }
        {
            var snapshot = RecoverableParking(context) ?? throw new InvalidOperationException("На карте нет вашего припаркованного шаттла.");
            var read = await _store.ReadFreeParkingAsync(snapshot);
            var yaml = read.Grid;
            if (generation != _freeParkingGeneration || GetContext(context.Console, context.Actor)?.Card != context.Card || OwnedShip(context) != null)
                throw new InvalidOperationException("Доступ к парковке изменился.");
            var map = _map.CreateMap(out var mapId, runMapInit: false); _map.SetPaused(mapId, true);
            EnsureComp<ParkingStagingSnapshotComponent>(map);
            var load = _loader.BeginIncrementalGridLoad(mapId, () => new StringReader(yaml),
                new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
            var recall = _freeParkingRecall = new FreeParkingRecall(context, snapshot, map, load);
            var grid = await recall.Completion.Task;
            var claimed = false;
            try
            {
                var ship = Comp<ParkingShuttleComponent>(grid);
                CheckVoucherParkingAllowed(grid); ValidateParkingOrigin(context.Actor, ship);
                if (ship.ParkingProfile != snapshot.Profile || ship.OriginFaction != snapshot.OriginFaction || ship.Identity != snapshot.Identity || ship.OwnerAccount != snapshot.Owner || !ship.Parked || OwnedShip(context) != null)
                    throw new InvalidDataException("Free-parking snapshot ownership mismatch.");
                if (dock == null || !AvailableDocks(context.Console).Any(d => d.Entity == dock))
                    throw new InvalidOperationException("Выберите свободный шлюз.");
                var targetDock = GetEntity(dock.Value); var target = Transform(targetDock).GridUid;
                if (target == null || SelectedDockConfig(grid, target.Value, targetDock) == null)
                    throw new InvalidOperationException("Шаттл не помещается у выбранного шлюза.");
                CheckParkingFleetLimit(snapshot.Vessel, snapshot.Identity);
                claimed = await _store.ClaimFreeParkingAsync(snapshot);
                if (!claimed) throw new InvalidOperationException("Снимок парковки изменился.");
                await WaitFreeParkingPublicationTurn();
                if (generation != _freeParkingGeneration || GetContext(context.Console, context.Actor)?.Card != context.Card || OwnedShip(context) != null)
                    throw new InvalidOperationException("Доступ к парковке изменился.");
                ValidateParkingOrigin(context.Actor, ship);
                ship.ParkingCharacter = context.Character;
                _activeShips[snapshot.Identity] = grid;
                _parkingMaps[grid] = map;
                _shipyard.BindParkingDeed(grid, context.Card, snapshot.Name, Name(context.Actor));
                RememberFreeParkingCard(grid, context.Card);
                Deliver(context, grid, dock);
                CompleteFreeParkingPublication(snapshot);
            }
            catch
            {
                if (_activeShips.GetValueOrDefault(snapshot.Identity) == grid) _activeShips.Remove(snapshot.Identity);
                if (TryComp<ShuttleDeedComponent>(context.Card, out var deed) && deed.ShuttleUid == grid) RemComp<ShuttleDeedComponent>(context.Card);
                RehideFreeParkingGrid(grid, map);
                EntityManager.System<StagedMapDeletionSystem>().Queue(grid);
                if (_parkingMaps.Remove(grid, out var hiddenMap)) EntityManager.System<StagedMapDeletionSystem>().Queue(hiddenMap);
                EntityManager.System<StagedMapDeletionSystem>().Queue(map);
                _freeParkingGarbage.Enqueue(load.Entities.GetEnumerator());
                if (claimed) await _store.AbortFreeParkingClaimAsync(snapshot);
                throw;
            }
        }
    }

    private async Task WaitFreeParkingPublicationTurn()
    {
        if (_freeParkingPublicationTick == _timing.CurTick.Value)
        { _freeParkingPublicationTurn = new TaskCompletionSource(); await _freeParkingPublicationTurn.Task; }
        _freeParkingPublicationTick = _timing.CurTick.Value;
    }

    private void CompleteFreeParkingPublication(FreeParkingSnapshot snapshot)
    {
        var start = Stopwatch.GetTimestamp();
        _freeParkingWrite = _store.CompleteFreeParkingClaim(snapshot);
        _freeParkingRecovered.Remove(snapshot.Identity);
        Log.Info($"Free parking publication {snapshot.Identity}: fence_ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3}");
    }

    private void RehideFreeParkingGrid(EntityUid grid, EntityUid? privateMap = null)
    {
        if (TerminatingOrDeleted(grid)) return;
        var map = privateMap ?? _parkingMaps.GetValueOrDefault(grid);
        if (!map.IsValid() || TerminatingOrDeleted(map))
        { map = _map.CreateMap(out var mapId, runMapInit: false); _map.SetPaused(mapId, true); }
        _docking.UndockDocks(grid);
        if (_stations.GetOwningStation(grid) is { } station) _stations.RemoveGridFromStation(station, grid);
        _transform.SetCoordinates(grid, new EntityCoordinates(map, System.Numerics.Vector2.Zero));
        Comp<ParkingShuttleComponent>(grid).Parked = true;
        _parkingMaps[grid] = map;
    }

    private void UpdateFreeParkingRecall()
    {
        var work = _freeParkingRecall!;
        try
        {
            if (GetContext(work.Context.Console, work.Context.Actor)?.Card != work.Context.Card)
                throw new OperationCanceledException("Доступ к парковке изменился.");
            var start = Stopwatch.GetTimestamp();
            for (var step = 0; step < FreeParkingStepsPerTick; step++)
            {
                if (work.Equipment != null)
                {
                    if (!work.Equipment.MoveNext())
                    {
                        work.Equipment.Dispose(); work.Load.Dispose(); _freeParkingRecall = null;
                        work.Completion.TrySetResult(work.Load.Grid!.Value.Owner); return;
                    }
                }
                else if (!work.Load.MoveNext())
                {
                    if (work.Load.Grid is not { } grid) throw new InvalidDataException("Не удалось загрузить припаркованный шаттл.");
                    EnsureComp<ParkingStagingSnapshotComponent>(grid.Owner);
                    work.Equipment = RestoreSnapshotEquipmentSteps(grid.Owner, connectDevices: true).GetEnumerator();
                }
                if (Stopwatch.GetElapsedTime(start) >= FreeParkingBudget) break;
            }
        }
        catch (Exception error)
        {
            work.Equipment?.Dispose(); work.Load.Dispose(); _freeParkingRecall = null;
            _freeParkingGarbage.Enqueue(work.Load.Entities.GetEnumerator());
            EntityManager.System<StagedMapDeletionSystem>().Queue(work.Map);
            work.Completion.TrySetException(error);
        }
    }

    private void StopFreeParking(bool invalidateCurrent)
    {
        _freeParkingGeneration++;
        StopRecoveredParkingSales();
        _freeParkingPublicationTurn?.TrySetCanceled(); _freeParkingPublicationTurn = null;
        _store.AbortAnyFreeParkingClaimAsync().GetAwaiter().GetResult();
        if (_freeParkingRecall is { } recall)
        {
            recall.Equipment?.Dispose(); recall.Load.Dispose(); _freeParkingRecall = null;
            _freeParkingGarbage.Enqueue(recall.Load.Entities.GetEnumerator());
            EntityManager.System<StagedMapDeletionSystem>().Queue(recall.Map);
            recall.Completion.TrySetException(new OperationCanceledException("Parking lifecycle ended."));
        }
        foreach (var capture in _freeParkingCaptures.Values)
        { capture.Cancelled = true; capture.Steps?.Dispose(); capture.Steps = null; }
        _freeParkingCapture = null; _freeParkingQueue.Clear();
        _freeParkingDirty.Clear(); _freeParkingDirtySet.Clear();
        // Explicit lifecycle boundary only: never block the simulation update on disk.
        try { _freeParkingWrite?.GetAwaiter().GetResult(); }
        catch (Exception error) { Log.Error($"Free parking final write failed: {error}"); }
        if (invalidateCurrent)
        {
            var current = _freeParkingCaptures.Values.Select(c => c.Snapshot.Identity).ToHashSet();
            var revisions = _freeParkingCaptures.Values.Select(c => (c.Snapshot.Identity, c.Snapshot.Revision))
                .Concat(_freeParkingRecovered.Values.Where(s => current.Contains(s.Identity)).Select(s => (s.Identity, s.Revision))).Distinct().ToArray();
            _store.InvalidateFreeParkingAsync(revisions).GetAwaiter().GetResult();
            foreach (var capture in _freeParkingCaptures.Values)
            {
                _freeParkingRecovered.Remove(capture.Snapshot.Identity);
            }
        }
        _freeParkingCaptures.Clear();
    }

    private void QueueRecoveredParkingRoundEndSales()
    {
        // Index completion is metadata-only; no grid loading or valuation occurs at the round boundary.
        if (_freeParkingIndex is { } index)
        {
            foreach (var snapshot in index.GetAwaiter().GetResult()) _freeParkingRecovered.TryAdd(snapshot.Identity, snapshot);
            _freeParkingIndex = null;
        }
        var remaining = _freeParkingRecovered.Values.Where(s => !_activeShips.ContainsKey(s.Identity)).ToArray();
        _store.MarkFreeParkingSalesAsync(remaining).GetAwaiter().GetResult();
        foreach (var snapshot in remaining) _freeParkingRecovered.Remove(snapshot.Identity);
        RequestRecoveredParkingSaleScan();
    }

    private bool TryCopyFreeParkingVesselReservations(Dictionary<Guid, string> destination)
    {
        if (_freeParkingIndex != null) return false;
        foreach (var snapshot in _freeParkingRecovered.Values)
            if (!string.IsNullOrEmpty(snapshot.Vessel)) destination[snapshot.Identity] = snapshot.Vessel;
        foreach (var capture in _freeParkingCaptures.Values)
            if (!string.IsNullOrEmpty(capture.Snapshot.Vessel)) destination[capture.Snapshot.Identity] = capture.Snapshot.Vessel;
        return true; // Pending sales cannot be recalled and do not reserve the active fleet quota.
    }

    private void ForgetFreeParkingAfterPaidSave(Guid identity) => _freeParkingRecovered.Remove(identity);

    private bool UpdateFreeParkingGarbage()
    {
        var start = Stopwatch.GetTimestamp();
        for (var step = 0; step < FreeParkingStepsPerTick && _freeParkingGarbage.TryPeek(out var entities); step++)
        {
            if (!entities.MoveNext()) { entities.Dispose(); _freeParkingGarbage.Dequeue(); continue; }
            EntityManager.System<StagedMapDeletionSystem>().Queue(entities.Current);
            if (Stopwatch.GetElapsedTime(start) >= FreeParkingBudget) break;
        }
        return _freeParkingGarbage.Count > 0;
    }
}
