using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._Mono.ShipRepair;
using Content.Server.Database;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Maps;
using Content.Shared.Materials;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.Research.Prototypes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private ShipRepairSystem _repairs = default!;
    private readonly Dictionary<Guid, RepairQuote> _repairQuotes = new();
    private readonly Dictionary<EntityUid, RepairJob> _repairJobs = new();
    private TimeSpan _repairRecoveryAfter;
    private bool _repairRecoveryBusy;
    private sealed record RepairQuote(Guid Token, EntityUid Grid, Guid Ship, Guid Version, string Fingerprint,
        ParkingRepairPart[] Parts, long Materials, long Credits, long Coins, int Seconds, TimeSpan Expires, int Profile);
    private sealed class RepairJob(ShipRepairPurchase purchase, RepairQuote quote, EntityUid console, EntityUid actor, TimeSpan due)
    {
        public ShipRepairPurchase Purchase = purchase;
        public RepairQuote Quote = quote;
        public EntityUid Console = console, Actor = actor;
        public TimeSpan Due = due;
        public bool Applied, Refunding, Finishing;
    }

    private ParkingRepairView BuildRepairView(EntityUid grid, Guid user)
    {
        var ship = Comp<ParkingShuttleComponent>(grid);
        var view = new ParkingRepairView { Ship = ship.Identity.ToString(), Parked = ship.Parked,
            Parts = ShipyardParts(grid), Current = RepairVisuals(grid) };
        if (_repairJobs.TryGetValue(grid, out var job))
        {
            view.Working = true;
            view.Remaining = Math.Max(0, (int)Math.Ceiling((job.Due - _timing.RealTime).TotalSeconds));
            view.Selected = job.Quote.Parts.Select(p => p.Id).ToArray();
        }
        else if (_repairQuotes.TryGetValue(user, out var quote) && quote.Grid == grid && quote.Expires > _timing.RealTime)
        {
            view.Token = quote.Token; view.Credits = quote.Credits; view.Coins = quote.Coins;
            view.Materials = quote.Materials; view.Seconds = quote.Seconds;
            view.Selected = quote.Parts.Select(p => p.Id).ToArray();
        }
        return view;
    }

    private List<ParkingRepairVisual> RepairVisuals(EntityUid uid)
    {
        var grid = Comp<MapGridComponent>(uid);
        var result = _map.GetAllTiles(uid, grid).Select(t => new ParkingRepairVisual("", _map.TileCenterToVector((uid, grid), t.GridIndices),
            Angle.Zero, t.Tile.TypeId, t.Tile.Variant)).ToList();
        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            var transform = Transform(child);
            if (transform.Anchored && transform.ParentUid == uid && Prototype(child) is { } prototype)
                result.Add(new(prototype.ID, transform.LocalPosition, transform.LocalRotation, 0));
        }
        return result;
    }

    private string RepairFingerprint(EntityUid grid) => ParkingStore.Hash(JsonSerializer.Serialize(new
    {
        Parts = ShipyardParts(grid).Select(p => new { p.Id, p.Prototype, X = p.Position.X, Y = p.Position.Y,
            Rotation = p.Rotation.Theta, p.Tile, p.Kind, p.Seconds, p.Charges, Original = p.Original?.ToString(), Source = p.Source?.ToString() }),
        Current = RepairVisuals(grid).Select(p => new { p.Prototype, X = p.Position.X, Y = p.Position.Y, Rotation = p.Rotation.Theta, p.Tile, p.Variant })
    }));

    private List<ParkingRepairPart> ShipyardParts(EntityUid grid)
    {
        // NetEntity links to off-ship objects are deliberately omitted by the parking serializer.
        // Reconnect by durable purchase identity before deciding that an object was destroyed.
        // An original installed on another vessel must never become a paid duplicate here.
        if (TryComp<ShipRepairDataComponent>(grid, out var data))
        {
            var missing = data.Chunks.Values.SelectMany(c => c.Entities.Values).Where(spec => spec.ParkingOriginalIdentity != Guid.Empty &&
                (spec.OriginalEntity is not { } net || !TryGetEntity(net, out var uid) || uid is not { } entity || TerminatingOrDeleted(entity))).ToArray();
            if (missing.Length > 0)
            {
                var wanted = missing.Select(s => s.ParkingOriginalIdentity).ToHashSet();
                var live = new Dictionary<Guid, EntityUid>();
                var originals = EntityQueryEnumerator<ParkingOriginalComponent>();
                var identity = Comp<ParkingShuttleComponent>(grid).Identity;
                while (originals.MoveNext(out var entity, out var original))
                    if (original.Shuttle == identity && wanted.Contains(original.Identity) && !TerminatingOrDeleted(entity)) live.TryAdd(original.Identity, entity);
                foreach (var spec in missing)
                    if (live.TryGetValue(spec.ParkingOriginalIdentity, out var original)) spec.OriginalEntity = GetNetEntity(original);
                if (live.Count > 0) Dirty(grid, data);
            }
        }
        return _repairs.GetShipyardRepairs(grid).Where(part =>
        {
            if (part.Source is not { } net || !TryGetEntity(net, out var source) || source is not { } uid) return true;
            // A source fragment claimed as another ship can no longer be hauled into this repair order.
            return !HasComp<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(uid) &&
                (!TryComp<ParkingShuttleComponent>(uid, out var owner) || owner.Identity == Comp<ParkingShuttleComponent>(grid).Identity);
        }).ToList();
    }

    private async void OnRepairAction(EntityUid console, ParkingConsoleComponent component, ParkingRepairAction ev)
    {
        if (!_configuration.GetCVar(ShuttleParkingCVars.RepairEnabled)) return;
        if (_ticker.RunLevel == Content.Server.GameTicking.GameRunLevel.PostRound) return;
        var context = GetContext(console, ev.Actor);
        if (context == null) return;
        var user = context.Session.UserId.UserId;
        if (!_busy.Add(user)) return;
        var message = "";
        try
        {
            await WaitForSaveWriter();
            if (!_configuration.GetCVar(ShuttleParkingCVars.RepairEnabled))
                throw new InvalidOperationException("Ремонтная верфь отключена.");
            var grid = OwnedShip(context) ?? throw new InvalidOperationException("На ID-карте нет вашего шаттла.");
            if (!Comp<ParkingShuttleComponent>(grid).Parked) throw new InvalidOperationException("Для ремонта поставьте шаттл на бесплатную парковку.");
            if (_repairJobs.ContainsKey(grid)) throw new InvalidOperationException("Ремонт шаттла уже выполняется.");
            if (_repairs.HasActiveToolRepairs(grid)) throw new InvalidOperationException("Дождитесь завершения работы ремонтного устройства.");
            if (ev.Action == "quote")
            {
                if (ev.Selected.Length is < 1 or > 30000 || ev.Selected.Distinct().Count() != ev.Selected.Length)
                    throw new InvalidOperationException("Выберите элементы ремонта.");
                var available = ShipyardParts(grid).ToDictionary(p => p.Id);
                if (ev.Selected.Any(id => !available.ContainsKey(id))) throw new InvalidOperationException("Повреждения изменились. Обновите список.");
                var selected = ev.Selected.Select(id => available[id]).OrderBy(p => p.Kind == "tiles" ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
                ValidateRepairFloor(grid, selected);
                var fingerprint = RepairFingerprint(grid);
                var version = Comp<ShipRepairDataComponent>(grid).SnapshotVersion;
                var account = await _database.ReadEconomyAccountAsync(user, context.Character);
                if (!_configuration.GetCVar(ShuttleParkingCVars.RepairEnabled))
                    throw new InvalidOperationException("Ремонтная верфь отключена.");
                if (GetContext(console, ev.Actor)?.Card != context.Card || OwnedShip(context) != grid || _repairJobs.ContainsKey(grid))
                    throw new InvalidOperationException("Доступ к шаттлу изменился.");
                var materials = selected.Sum(part => RepairMaterialCost(part, account.MaterialFactors));
                var materialPrice = checked((long)Math.Ceiling(materials));
                var credits = checked((long)Math.Ceiling(materials * (1 + Math.Clamp(_configuration.GetCVar(ShuttleParkingCVars.RepairCommission), 0, 10))));
                credits = Math.Max(1, credits);
                var coins = Math.Max(1, (credits + Math.Max(1, _configuration.GetCVar(ShuttleParkingCVars.RepairCreditsPerCoin)) - 1) /
                    Math.Max(1, _configuration.GetCVar(ShuttleParkingCVars.RepairCreditsPerCoin)));
                var seconds = (int)Math.Clamp(Math.Ceiling(selected.Sum(p => Math.Max(.5, p.Seconds)) * 4), 1, 3600);
                _repairQuotes[user] = new(Guid.NewGuid(), grid, Comp<ParkingShuttleComponent>(grid).Identity,
                    version, fingerprint, selected, materialPrice,
                    credits, coins, seconds, _timing.RealTime + TimeSpan.FromMinutes(2), account.Profile);
                message = "Проверьте выбранные призраки, цену и время. Котировка действует 2 минуты.";
            }
            else if (ev.Action == "confirm")
            {
                if (!ev.Coins && HasComp<Content.Shared._Mono.Traits.Physical.IronmanComponent>(context.Actor))
                    throw new InvalidOperationException("Этот персонаж не может оплачивать ремонт кредитами.");
                if (!_repairQuotes.TryGetValue(user, out var quote) || quote.Token != ev.Token || quote.Expires <= _timing.RealTime || quote.Grid != grid)
                    throw new InvalidOperationException("Котировка истекла. Рассчитайте ремонт повторно.");
                ValidateRepairQuote(quote, user);
                if (!_bank.BeginExchangePayment(context.Session.UserId)) throw new InvalidOperationException("Дождитесь другой банковской операции.");
                try
                {
                    var purchase = new ShipRepairPurchase(quote.Token, user, quote.Profile, context.Character, quote.Ship, _shift,
                        ev.Coins ? 0 : quote.Credits, ev.Coins ? quote.Coins : 0,
                        DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
                    var job = new RepairJob(purchase, quote, console, ev.Actor, _timing.RealTime + TimeSpan.FromSeconds(ev.Coins ? 0 : quote.Seconds));
                    // Reserve the world before awaiting the durable debit. All other console actions see the lock.
                    _repairJobs.Add(grid, job);
                    Comp<ShipRepairDataComponent>(grid).ShipyardLocked = true;
                    try
                    {
                        var paid = await _database.PurchaseShipRepairAsync(purchase);
                        if (paid.State != "paid") throw new InvalidOperationException("Этот заказ уже завершён.");
                        ValidateRepairQuote(quote, user);
                        if (GetContext(console, ev.Actor)?.Card != context.Card || OwnedShip(context) != grid)
                            job.Refunding = true;
                        _repairQuotes.Remove(user);
                        message = ev.Coins ? "Оплата принята. Выполняется мгновенный ремонт." : "Оплата принята. Шаттл ремонтируется на верфи.";
                    }
                    catch
                    {
                        // A lost DB response may have committed. Keep the exact request for idempotent reconciliation.
                        job.Refunding = true;
                        throw;
                    }
                    finally { job.Due = ev.Coins || job.Refunding ? TimeSpan.Zero : _timing.RealTime + TimeSpan.FromSeconds(quote.Seconds); }
                }
                finally { _bank.EndExchangePayment(context.Session.UserId); }
                await _bank.PublishExchangeBalance(context.Session.UserId, context.Character);
            }
        }
        catch (Exception error) { message = error is InvalidOperationException or ArgumentException ? error.Message : "Ремонт пока недоступен. Обновите состояние."; Log.Warning($"Shipyard request failed: {error}"); }
        finally { _busy.Remove(user); }
        await Publish(console, ev.Actor, message);
    }

    private void ValidateRepairQuote(RepairQuote quote, Guid user)
    {
        if (!Exists(quote.Grid) || !TryComp<ParkingShuttleComponent>(quote.Grid, out var ship) || !ship.Parked ||
            ship.Identity != quote.Ship || ship.OwnerAccount != user || _activeShips.GetValueOrDefault(ship.Identity) != quote.Grid ||
            !TryComp<ShipRepairDataComponent>(quote.Grid, out var repair) || repair.SnapshotVersion != quote.Version ||
            RepairFingerprint(quote.Grid) != quote.Fingerprint)
            throw new InvalidOperationException("Состояние шаттла или ремонтный снимок изменились. Заказ отменён.");
    }

    private void ValidateRepairFloor(EntityUid grid, ParkingRepairPart[] parts)
    {
        var map = Comp<MapGridComponent>(grid);
        var floors = parts.Where(p => p.Kind == "tiles").Select(p => _map.LocalToTile(grid, map, new EntityCoordinates(grid, p.Position))).ToHashSet();
        foreach (var part in parts.Where(p => p.Kind != "tiles"))
        {
            var tile = _map.LocalToTile(grid, map, new EntityCoordinates(grid, part.Position));
            if (_map.GetTileRef(grid, map, tile).Tile.IsEmpty && !floors.Contains(tile))
                throw new InvalidOperationException("Выберите также отсутствующую палубу под оборудованием.");
        }
    }

    private double RepairMaterialCost(ParkingRepairPart part, Dictionary<string, double> factors)
    {
        // Price the exact matter charges consumed by the standard Ship Repair Device.
        // Use its current manufacturing recipe, not a guessed steel equivalent or resale value.
        var recipe = _prototypes.Index<LatheRecipePrototype>("ShipRepairDeviceAmmo");
        if (recipe.Result is not { } result || !_prototypes.Index(result).TryComp<LimitedChargesAmmoComponent>(out var ammo, _components))
            throw new InvalidOperationException("Не удалось оценить ремонтную материю.");
        var chargeYield = EntityManager.System<SharedChargesSystem>().PrototypeAmmoCharges(ammo);
        if (chargeYield <= 0) throw new InvalidOperationException("Не удалось оценить ремонтную материю.");
        var total = 0d;
        foreach (var (id, quantity) in recipe.Materials)
        {
            if (!_prototypes.TryIndex(id, out var material) || quantity < 0) throw new InvalidOperationException("Не удалось оценить материал ремонта.");
            total += quantity * (double)Math.Max(0, part.Charges) / chargeYield * (double)material.Price * factors.GetValueOrDefault(id.Id, 1);
        }
        if (!double.IsFinite(total) || total < 0) throw new InvalidOperationException("Не удалось рассчитать стоимость ремонта.");
        return total;
    }

    private void UpdateShipyardRepairs()
    {
        foreach (var (grid, job) in _repairJobs.ToArray())
            if (!job.Finishing && !_busy.Contains(job.Purchase.User) && _timing.RealTime >= job.Due)
                FinishRepairJob(grid, job);
        if (!_repairRecoveryBusy && _timing.RealTime >= _repairRecoveryAfter)
        {
            _repairRecoveryAfter = _timing.RealTime + TimeSpan.FromMinutes(1);
            RecoverExpiredRepairs();
        }
    }

    private async void FinishRepairJob(EntityUid grid, RepairJob job)
    {
        job.Finishing = true;
        try
        {
            // Retry the original debit to resolve ambiguous commit responses before either delivery or refund.
            var receipt = await _database.PurchaseShipRepairAsync(job.Purchase);
            if (receipt.State == "refunded") job.Refunding = true;
            if (receipt.State == "paid")
            {
                if (!job.Applied && !job.Refunding)
                {
                    try
                    {
                        ValidateRepairQuote(job.Quote, job.Purchase.User);
                        // Keep the previous crash snapshot during the paid wait.
                        // Invalidate it only when the parked hull is about to change.
                        await CancelFreeParkingSnapshot(grid);
                        ValidateRepairQuote(job.Quote, job.Purchase.User);
                        ApplyRepairPlan(grid, job.Quote.Parts);
                        job.Applied = true;
                    }
                    catch (Exception error) { job.Refunding = true; Log.Warning($"Ship repair cancelled: {error}"); }
                }
                await _database.FinishShipRepairAsync(job.Purchase.Id, job.Purchase.User, job.Refunding);
            }
            UnlockRepairJob(grid, job);
            await _bank.PublishExchangeBalance(new NetUserId(job.Purchase.User), job.Purchase.Slot);
            if (Exists(job.Actor) && Exists(job.Console)) await Publish(job.Console, job.Actor, job.Refunding ? "Заказ отменён. Оплата возвращена." : "Выбранные элементы шаттла восстановлены.");
        }
        catch (InsufficientLagoonCoinsException) { UnlockRepairJob(grid, job); }
        catch (InvalidOperationException error) when (error.Message.Contains("Недостаточно", StringComparison.Ordinal)) { UnlockRepairJob(grid, job); }
        catch (Exception error) { Log.Warning($"Ship repair reconciliation awaits retry: {error}"); job.Due = _timing.RealTime + TimeSpan.FromSeconds(10); }
        finally { job.Finishing = false; }
    }

    private void UnlockRepairJob(EntityUid grid, RepairJob job)
    {
        if (_repairJobs.GetValueOrDefault(grid) != job) return;
        _repairJobs.Remove(grid);
        if (TryComp<ShipRepairDataComponent>(grid, out var data)) data.ShipyardLocked = false;
        QueueFreeParkingSnapshot(grid);
    }

    private async void RecoverExpiredRepairs()
    {
        _repairRecoveryBusy = true;
        try
        {
            foreach (var purchase in await _database.ExpiredShipRepairsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                if (!_repairJobs.Values.Any(j => j.Purchase.Id == purchase.Id))
                    await _database.FinishShipRepairAsync(purchase.Id, purchase.User, true);
        }
        catch (Exception error) { Log.Warning($"Expired ship repair recovery deferred: {error.Message}"); }
        finally { _repairRecoveryBusy = false; }
    }

    private void ApplyRepairPlan(EntityUid grid, ParkingRepairPart[] parts)
    {
        var data = Comp<ShipRepairDataComponent>(grid);
        var oldTiles = parts.Where(p => p.Kind == "tiles").Select(p => _map.GetTileRef(grid, Comp<MapGridComponent>(grid),
            _map.LocalToTile(grid, Comp<MapGridComponent>(grid), new EntityCoordinates(grid, p.Position)))).ToArray();
        var references = data.Chunks.Values.SelectMany(c => c.Entities.Values).ToDictionary(s => s, s => s.OriginalEntity);
        var spawned = new List<EntityUid>();
        var moved = new List<(EntityUid Entity, EntityCoordinates Coordinates, Angle Rotation, bool Anchored)>();
        var oldOriginals = new Dictionary<Guid, EntityUid>(Comp<ParkingShuttleComponent>(grid).OriginalItems);
        try
        {
            foreach (var part in parts)
            {
                if (part.Original is { } net && TryGetEntity(net, out var original) && original is { } uid)
                {
                    var transform = Transform(uid);
                    moved.Add((uid, transform.Coordinates, transform.LocalRotation, transform.Anchored));
                }
                if (_repairs.ApplyShipyardRepair(grid, part) is not { } entity) continue;
                if (part.Original == null) spawned.Add(entity);
                var fields = part.Id.Split(':');
                var spec = data.Chunks[new Vector2i(int.Parse(fields[1]), int.Parse(fields[2]))].Entities[int.Parse(fields[3])];
                RestoreRepairProvenance(grid, entity, spec);
            }
        }
        catch
        {
            foreach (var entity in spawned) if (Exists(entity)) Del(entity);
            foreach (var original in moved)
            {
                _transform.Unanchor(original.Entity);
                _transform.SetCoordinates(original.Entity, original.Coordinates);
                _transform.SetLocalRotation(original.Entity, original.Rotation);
                if (original.Anchored) _transform.AnchorEntity(original.Entity);
            }
            Comp<ParkingShuttleComponent>(grid).OriginalItems = oldOriginals;
            foreach (var (spec, reference) in references) spec.OriginalEntity = reference;
            foreach (var tile in oldTiles) _map.SetTile(grid, Comp<MapGridComponent>(grid), tile.GridIndices, tile.Tile);
            throw;
        }
    }

    private void RestoreRepairProvenance(EntityUid grid, EntityUid entity, ShipRepairEntitySpecifier spec)
    {
        if (spec.ParkingOriginalIdentity != Guid.Empty)
        {
            var original = EnsureComp<ParkingOriginalComponent>(entity);
            original.Shuttle = Comp<ParkingShuttleComponent>(grid).Identity;
            original.Identity = spec.ParkingOriginalIdentity;
            original.BaselinePrototype = spec.ParkingBaseline;
            Comp<ParkingShuttleComponent>(grid).OriginalItems[original.Identity] = entity;
        }
        if (spec.PaidCapital > 0) EnsureComp<PurchasedCapitalComponent>(entity).Paid = spec.PaidCapital;
    }

    public void CaptureRepairProvenance(EntityUid grid)
    {
        if (!TryComp<ShipRepairDataComponent>(grid, out var repair)) return;
        foreach (var spec in repair.Chunks.Values.SelectMany(c => c.Entities.Values))
        {
            if (spec.OriginalEntity is not { } net || !TryGetEntity(net, out var uid) || uid is not { } entity || TerminatingOrDeleted(entity)) continue;
            if (TryComp<ParkingOriginalComponent>(entity, out var original))
            { spec.ParkingOriginalIdentity = original.Identity; spec.ParkingBaseline = original.BaselinePrototype; }
            if (TryComp<PurchasedCapitalComponent>(entity, out var capital)) spec.PaidCapital = capital.Paid;
        }
        Dirty(grid, repair);
    }

    public bool IsShipyardRepairing(EntityUid grid) => _repairJobs.ContainsKey(grid);
}
