using Content.Server._NF.Bank;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Roles.Jobs;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private readonly Queue<StoredVoucherSale> _storedVoucherSales = new();
    private bool _storedVoucherScanDone;
    private StoredVoucherSaleWork? _storedVoucherSale;
    private TimeSpan _storedVoucherRetry;

    private sealed class StoredVoucherSaleWork(StoredVoucherSale sale)
    {
        public readonly StoredVoucherSale Sale = sale;
        public EntityUid? Map;
        public MapLoaderSystem.IncrementalGridLoad? Load;
        public IEnumerator<bool>? Appraisal;
        public int? Price;
        public Task<bool>? Commit;
    }

    public bool TryCopyStoredVesselReservations(Dictionary<Guid, string> destination, bool includeLongTerm = true)
    {
        var ready = !includeLongTerm || _store.TryCopyStoredVesselReservations(destination);
        return TryCopyFreeParkingVesselReservations(destination) && ready;
    }

    /// <summary>Also covers cooperative allocations still in nullspace before their map/grid transforms are merged.</summary>
    public bool IsParkingStagingGrid(EntityUid grid)
    {
        if (HasComp<ParkingStagingSnapshotComponent>(grid) ||
            TryComp<TransformComponent>(grid, out var transform) && transform.MapUid is { } map &&
            HasComp<ParkingStagingSnapshotComponent>(map)) return true;
        return _storedVoucherSale?.Load?.Entities.Contains(grid) == true ||
            _freeParkingRecall?.Load.Entities.Contains(grid) == true || _freeSale?.Load?.Entities.Contains(grid) == true;
        // Legacy long-term LoadSnapshot is synchronous: its private map is marked before loading and
        // its completed grid before returning. No pending nullspace allocation can survive that call.
    }

    private void UpdateStoredVoucherSales()
    {
        if (!_storedVoucherScanDone)
        {
            if (!_store.StoredVoucherSales.IsCompleted) return;
            _storedVoucherScanDone = true;
            try
            {
                foreach (var pending in _store.StoredVoucherSales.GetAwaiter().GetResult()) _storedVoucherSales.Enqueue(pending);
            }
            catch (Exception error) { Log.Error($"Stored shuttle provenance index failed; saves retained: {error}"); }
        }
        if (_storedVoucherSale == null && _timing.RealTime >= _storedVoucherRetry && _storedVoucherSales.TryDequeue(out var sale))
            _storedVoucherSale = new(sale);
        if (_storedVoucherSale is not { } work) return;
        try
        {
            if (work.Commit is { } commit)
            {
                if (!commit.IsCompleted) return;
                if (commit.GetAwaiter().GetResult())
                {
                    Log.Info($"Sold stored voucher shuttle {work.Sale.Ship.Identity} for {work.Price} credits to owner {work.Sale.Owner} profile {work.Sale.Ship.OwnerProfile}.");
                    _nextPayoutRetry = TimeSpan.Zero;
                }
                FinishStoredVoucherSale(work);
                return;
            }
            if (work.Load == null)
            {
                var snapshot = work.Sale.Ship;
                if (snapshot.OwnerProfile <= 0 || snapshot.Checksum != ParkingStore.Hash(snapshot.Grid))
                    throw new InvalidDataException("Voucher sale requires a valid snapshot and proven immutable owner profile; saved ship retained.");
                work.Map = _map.CreateMap(out var mapId, runMapInit: false);
                EnsureComp<ParkingStagingSnapshotComponent>(work.Map.Value);
                _map.SetPaused(mapId, true);
                work.Load = _loader.BeginIncrementalGridLoad(mapId, () => new StringReader(snapshot.Grid),
                    new DeserializationOptions { LogInvalidEntities = false }, allowPostMapInit: true);
            }
            var start = Stopwatch.GetTimestamp();
            for (var step = 0; step < 16; step++)
            {
                if (work.Appraisal != null)
                {
                    if (!work.Appraisal.MoveNext())
                    {
                        if (work.Price is not { } price) throw new InvalidDataException("Voucher appraisal returned no price.");
                        if (_freeParkingWrite is { IsCompleted: false } ||
                            (_saveWork?.Finish ?? _saveWork?.Commit) is { IsCompleted: false }) return;
                        var payout = new ParkingPayout(work.Sale.Ship.Identity, work.Sale.Owner, work.Sale.Character, price,
                            work.Sale.Ship.OwnerProfile, new() { ["Industry"] = price });
                        work.Commit = _store.CommitStoredVoucherSaleAsync(work.Sale, payout);
                        _freeParkingWrite = work.Commit;
                        return;
                    }
                }
                else if (!work.Load.MoveNext())
                {
                    if (work.Load.Grid is not { } grid || work.Load.RequiresSynchronousLoad)
                        throw new InvalidDataException("Voucher snapshot requires a complete incremental load; saved ship retained.");
                    if (!TryComp<ParkingShuttleComponent>(grid.Owner, out var original) ||
                        original.Identity != work.Sale.Ship.Identity || original.OwnerAccount != work.Sale.Owner ||
                        original.ParkingProfile != work.Sale.Ship.OwnerProfile)
                        throw new InvalidDataException("Voucher snapshot owner/identity does not match durable sale provenance; source retained.");
                    EnsureComp<ParkingStagingSnapshotComponent>(grid.Owner);
                    work.Appraisal = _shipyard.AppraiseParkedShuttleSteps(grid.Owner, price => work.Price = price).GetEnumerator();
                }
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 1) break;
            }
        }
        catch (Exception error)
        {
            Log.Error($"Stored voucher shuttle {work.Sale.Ship.Identity} sale pending; source retained: {error}");
            _storedVoucherSales.Enqueue(work.Sale);
            _storedVoucherRetry = _timing.RealTime + TimeSpan.FromMinutes(1);
            FinishStoredVoucherSale(work);
        }
    }

    private void FinishStoredVoucherSale(StoredVoucherSaleWork work)
    {
        work.Appraisal?.Dispose();
        work.Load?.Dispose();
        if (work.Load != null) _freeParkingGarbage.Enqueue(work.Load.Entities.GetEnumerator());
        if (work.Map is { } map) _freeParkingGarbage.Enqueue(new[] { map }.AsEnumerable().GetEnumerator());
        if (ReferenceEquals(_storedVoucherSale, work)) _storedVoucherSale = null;
    }

    private void StopStoredVoucherSales()
    {
        if (_storedVoucherSale is not { } work) return;
        if (work.Commit != null)
            try { work.Commit.GetAwaiter().GetResult(); } catch { /* Source or receipt remains durable. */ }
        else _storedVoucherSales.Enqueue(work.Sale);
        FinishStoredVoucherSale(work);
    }

    /// <summary>Deeds are a legacy positive witness only; a later deed change cannot clear the purchase flag.</summary>
    public bool IsVoucherPurchasedShuttle(EntityUid grid)
    {
        var voucher = TryComp<ShuttleDeedComponent>(grid, out var deed) && deed.PurchasedWithVoucher;
        if (TryComp<ParkingShuttleComponent>(grid, out var ship))
        {
            ship.PurchasedWithVoucher |= voucher;
            voucher |= ship.PurchasedWithVoucher;
        }
        return voucher;
    }

    private void CheckVoucherParkingAllowed(EntityUid grid)
    {
        if (IsVoucherPurchasedShuttle(grid))
            throw new InvalidOperationException("Шаттлы, приобретённые за ваучер, нельзя парковать или сохранять.");
    }

    public string? GetParkingOriginFaction(EntityUid actor)
    {
        if (!TryComp<MindContainerComponent>(actor, out var mind) ||
            !EntityManager.System<SharedJobSystem>().MindTryGetJob(mind.Mind, out var job)) return null;
        return job.AssignedCompany.Id;
    }

    /// <summary>Uses the attached body's immutable bank identity and server mind job, never its inserted ID card.</summary>
    public void ValidateParkingOrigin(EntityUid actor, ParkingShuttleComponent ship)
    {
        if (!TryComp<BankCharacterComponent>(actor, out var character) ||
            !ParkingOriginMatches(ship.OwnerAccount, ship.ParkingProfile, ship.OriginFaction,
                character.User, character.Profile, GetParkingOriginFaction(actor)))
            throw new InvalidOperationException("Шаттл доступен только исходному персонажу в исходной фракции. Для старого сохранения требуется подтверждённое происхождение.");
    }

    private void ValidateStoredParkingOrigin(EntityUid actor, StoredShuttle ship)
    {
        if (ship.PurchasedWithVoucher)
            throw new InvalidOperationException("Ваучерный шаттл подлежит продаже с выплатой владельцу и недоступен для выдачи.");
        if (!TryComp<BankCharacterComponent>(actor, out var character) ||
            !ParkingOriginMatches(character.User, ship.OwnerProfile, ship.OriginFaction,
                character.User, character.Profile, GetParkingOriginFaction(actor)))
            throw new InvalidOperationException("Сохранение доступно только исходному персонажу в исходной фракции. Происхождение старого сохранения должно быть подтверждено.");
    }

    public static bool ParkingOriginMatches(Guid owner, int profile, string? faction,
        Guid currentOwner, int currentProfile, string? currentFaction) =>
        owner != Guid.Empty && owner == currentOwner && profile > 0 && profile == currentProfile &&
        !string.IsNullOrEmpty(faction) && string.Equals(faction, currentFaction, StringComparison.Ordinal);

    public void CheckParkingFleetLimit(string? vessel, Guid identity)
    {
        if (!string.IsNullOrEmpty(vessel) && !EntityManager.System<Content.Server._Mono.Ships.Systems.ShuttleRestrictionsSystem>()
                .CanRestoreParkedVessel(vessel, identity))
            throw new InvalidOperationException("Лимит шаттлов этого типа занят. Возврат станет доступен после освобождения лимита.");
    }
}
