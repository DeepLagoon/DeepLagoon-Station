using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Cargo.Systems;
using Content.Server.GameTicking;
using Content.Server.Research.Disk;
using Content.Server.Nutrition.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.Ghost;
using Content.Shared.GameTicking;
using Content.Shared.Materials;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Players;
using Content.Shared.Research.TechnologyDisk.Components;
using Content.Shared.Stacks;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Procurement;

[CVarDefs]
public sealed class ProcurementCVars
{
    // There is intentionally no enforcement switch in this first stage.
    public static readonly CVarDef<bool> ShadowEnabled =
        CVarDef.Create("procurement.shadow_enabled", false, CVar.SERVERONLY);
}

/// <summary>Captures bounded immutable snapshots on the game thread; never changes a sale.</summary>
public sealed class ProcurementSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;

    private const int ManifestLimit = 4096;
    private ProcurementJournal? _journal;
    private bool _enabled;
    private TimeSpan _nextSample;
    private int _captureErrors;
    public bool Enabled => _enabled || _config.GetCVar(Content.Server._DeepLagoon.Exchange.ExchangeCVars.Enabled) ||
        _config.GetCVar(Content.Server._DeepLagoon.Economy.SectorEconomyCVars.Enabled);
    public bool ProtectsCrypto(EntityUid entity) => _config.GetCVar(Content.Server._DeepLagoon.Exchange.ExchangeCVars.Enabled) &&
        Content.Server.Database.ExchangeMarket.Assets.Any(a => a.CoinPrototype == MetaData(entity).EntityPrototype?.ID);
    public long RecordedSales => _journal?.Sales ?? 0;
    public Task FlushAsync() => _journal?.FlushAsync() ?? Task.CompletedTask;
    public string Status => $"enabled={_enabled}, capture_errors={_captureErrors}, written={_journal?.Written ?? 0}, dropped={_journal?.Dropped ?? 0}, fault={_journal?.Fault ?? "none"}\n{_journal?.Status ?? "not_started"}";
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public override void Initialize() => _config.OnValueChanged(ProcurementCVars.ShadowEnabled, SetEnabled, true);

    public override void Shutdown()
    {
        _config.UnsubValueChanged(ProcurementCVars.ShadowEnabled, SetEnabled);
        if (_journal == null)
            return;
        Record(new(Guid.NewGuid(), Now, _ticker.RoundId, "population", Reason: "observation_stopped"));
        // Only shutdown may wait for the audit writer; ordinary game ticks never perform SQLite IO.
        _journal.Complete();
        var drain = _journal.DisposeAsync().AsTask();
        if (!drain.Wait(TimeSpan.FromSeconds(2)))
            Log.Warning("Procurement observation queue did not drain before shutdown; treat the tail as incomplete.");
    }

    private void SetEnabled(bool value)
    {
        if (!value && _enabled)
            Record(new(Guid.NewGuid(), Now, _ticker.RoundId, "population", Reason: "observation_disabled"));
        _enabled = value;
        if (!value)
            return;
        _journal ??= new ProcurementJournal(_resources.UserData.RootDir is { } root
            ? Path.Combine(root, "economy-procurement-shadow.db") : ":memory:", Now);
        SamplePopulation();
    }

    public override void Update(float frameTime)
    {
        if (!_enabled || _timing.RealTime < _nextSample)
            return;
        _nextSample = _timing.RealTime + TimeSpan.FromSeconds(10);
        SamplePopulation();
    }

    private void SamplePopulation()
    {
        var connected = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        foreach (var session in _players.Sessions)
        {
            if (_admins.IsAdmin(session))
                continue;
            connected.Add(session.UserId.UserId);
            if (_ticker.RunLevel != GameRunLevel.InRound || session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { } actor || HasComp<GhostComponent>(actor) || _afk.IsAfk(session) ||
                !TryComp<MobStateComponent>(actor, out var state) || state.CurrentState is not (MobState.Alive or MobState.Critical))
                continue;
            active.Add(session.UserId.UserId);
        }
        Record(new(Guid.NewGuid(), Now, _ticker.RoundId, "population", ActivePlayers: active.Count, ConnectedPlayers: connected.Count));
    }

    private void Record(ProcurementObservation observation)
    {
        if (!_enabled)
            return;
        _journal?.TryRecord(observation);
    }

    public ProcurementSale? CapturePallet(EntityUid console, EntityUid grid, EntityUid? actor,
        IEnumerable<ProcurementRoot> quotes, IReadOnlyDictionary<int, ProcurementValuation> valuations, long cash, string currency)
    {
        if (!Enabled)
            return null;
        try
        {
            var roots = quotes.ToArray();
            var items = new List<ProcurementItem>();
            var pending = new Stack<(EntityUid Entity, int? Parent)>();
            foreach (var root in roots)
                pending.Push((new EntityUid(root.Entity), null));
            var seen = new HashSet<EntityUid>();
            var started = Stopwatch.GetTimestamp();
            while (items.Count < ManifestLimit && pending.TryPop(out var entry))
            {
                if (!seen.Add(entry.Entity))
                    continue;
                var item = entry.Entity;
                var quantity = TryComp<StackComponent>(item, out var stack) ? stack.Count : 1;
                var materials = TryComp<PhysicalCompositionComponent>(item, out var composition)
                    ? composition.MaterialComposition.ToDictionary(p => p.Key.Id, p => (double) p.Value * quantity) : new();
                var stored = TryComp<MaterialStorageComponent>(item, out var storage)
                    ? storage.Storage.ToDictionary(p => p.Key.Id, p => (double) p.Value) : new();
                var gases = new Dictionary<string, double>();
                var air = TryComp<GasCanisterComponent>(item, out var canister) ? canister.Air
                    : TryComp<GasTankComponent>(item, out var tank) ? tank.Air : null;
                if (air != null)
                    for (var i = 0; i < Atmospherics.TotalNumberOfGases; i++)
                        if (air.GetMoles(i) > 0)
                            gases[((Gas) i).ToString()] = air.GetMoles(i);
                valuations.TryGetValue(item.Id, out var valuation);
                items.Add(new(item.Id, entry.Parent, MetaData(item).EntityPrototype?.ID, quantity,
                    stack?.StackTypeId.Id, Classify(item), valuation?.OwnBaseAppraisal, valuation?.OwnPayout, materials, stored, gases));
                if (TryComp<ContainerManagerComponent>(item, out var containers))
                    foreach (var container in containers.Containers.Values)
                        foreach (var child in container.ContainedEntities)
                            pending.Push((child, item.Id));
                if (items.Count % 64 == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > 5)
                    break; // Mark incomplete; a pathological container must not stall a game tick.
            }
            // Container shell values remain in actual root prices; a plain crate does not turn ore into industrial output.
            var childrenByParent = items.Where(i => i.Parent != null).ToLookup(i => i.Parent!.Value);
            var categories = new Dictionary<int, ProcurementCategory>();
            foreach (var own in items.AsEnumerable().Reverse())
            {
                var category = own.SuggestedCategory;
                if (own.Prototype?.StartsWith("Crate", StringComparison.Ordinal) == true)
                {
                    var children = childrenByParent[own.Entity].Where(i => i.OwnBaseAppraisal is > 0).Select(i => categories.GetValueOrDefault(i.Entity, ProcurementCategory.Unclassified))
                    .Where(c => c != ProcurementCategory.Principal).Distinct().ToArray();
                    category = children.Length == 0 ? ProcurementCategory.Industry : children.Length == 1 ? children[0] : ProcurementCategory.Unclassified;
                }
                categories[own.Entity] = category;
            }
            roots = roots.Select(r => r with { SuggestedCategory = categories.GetValueOrDefault(r.Entity, ProcurementCategory.Unclassified) }).ToArray();
            var principal = items.Where(i => i.Stack == "Credit" && i.OwnPayout is > 0).Sum(i => (long) i.OwnPayout!.Value);
            actor = actor is { } candidate && Exists(candidate) ? candidate : null;
            var player = actor is { } entity && TryComp<ActorComponent>(entity, out var session)
                ? session.PlayerSession.UserId.UserId : (Guid?) null;
            return new(Guid.NewGuid(), Now, _ticker.RoundId, player, actor is { } character ? Name(character) : null,
                MetaData(console).EntityPrototype?.ID, roots, items.ToArray(), cash, principal, new(), pending.Count == 0, currency);
        }
        catch (Exception e)
        {
            _captureErrors++;
            Log.Warning($"Procurement manifest capture failed ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}); sale behavior is unchanged.");
            return null;
        }
    }

    internal ProcurementCategory Classify(EntityUid item)
    {
        if (TryComp<StackComponent>(item, out var stack) && stack.StackTypeId == "Credit")
            return ProcurementCategory.Principal;
        if (HasComp<GasCanisterComponent>(item) || HasComp<GasTankComponent>(item))
            return ProcurementCategory.Gas;
        if (HasComp<MaterialComponent>(item))
            return ProcurementCategory.Materials;
        // Edible anatomy is not a prepared meal and must not consume the food procurement pool.
        if (HasComp<Content.Shared.Body.Organ.OrganComponent>(item) || HasComp<Content.Shared.Body.Part.BodyPartComponent>(item) ||
            HasComp<Content.Server.Salvage.ExpeditionSalvageComponent>(item))
            return ProcurementCategory.Industry;
        if (HasComp<FoodComponent>(item))
            return ProcurementCategory.Food;
        if (HasComp<TechnologyDiskComponent>(item) || HasComp<ResearchDiskComponent>(item))
            return ProcurementCategory.Recovery;
        return ProcurementCategory.Industry;
    }

    public void CommitPallet(ProcurementSale? sale)
    {
        if (sale == null)
            return;
        Record(new(sale.Id, Now, sale.Round, "pallet_sale", Sale: sale));
        RaiseLocalEvent(new ProcurementSaleObserved(sale));
    }

    public void RecordBounty(Guid? parent, string bounty, string prototype, long amount)
    {
        if (!_enabled || amount <= 0)
            return;
        var currency = _prototypes.TryIndex<EntityPrototype>(prototype, out var proto) &&
            proto.TryComp<StackComponent>(out var stack, _factory) ? stack.StackTypeId.Id : "prototype:" + prototype;
        Record(new(Guid.NewGuid(), Now, _ticker.RoundId, "bounty_reward", Amount: amount,
            Account: prototype, Reason: bounty, Currency: currency, Parent: parent));
    }

    public void RecordTreasury(string account, string reason, long delta, long balance)
    {
        if (_enabled)
            Record(new(Guid.NewGuid(), Now, _ticker.RoundId, "treasury_flow", Amount: delta,
                Account: account, Reason: reason, Balance: balance));
    }

    public void RecordParkingManifest(ProcurementParkingManifest manifest)
        => Record(new(manifest.Operation, Now, _ticker.RoundId, "parking_manifest", Parking: manifest));

    public void CaptureParkingManifest(Guid operation, Guid player, int slot, long payout,
        double confiscated, double materialValue, double research, int unpriced, IEnumerable<EntityUid> retained)
    {
        if (!_enabled)
            return;
        var materials = new Dictionary<string, double>();
        var scanned = 0;
        var complete = true;
        foreach (var entity in retained)
        {
            if (++scanned > ManifestLimit) { complete = false; break; }
            if (!TryComp<MaterialStorageComponent>(entity, out var storage))
                continue;
            foreach (var (material, quantity) in storage.Storage)
                materials[material.Id] = materials.GetValueOrDefault(material.Id) + quantity;
        }
        RecordParkingManifest(new(operation, player, slot, payout, confiscated, materialValue,
            research, materials, unpriced, complete));
    }

    public void RecordParkingPayment(Guid operation, Guid player, int slot, long amount, long balance)
    {
        if (_enabled)
            Record(new(operation, Now, _ticker.RoundId, "parking_payment", Player: player,
                Amount: amount, Balance: balance, Slot: slot));
    }
}
