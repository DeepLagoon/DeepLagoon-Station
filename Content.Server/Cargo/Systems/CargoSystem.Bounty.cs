using Robust.Shared.Prototypes;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Cargo.Components;
using Content.Server.Labels;
using Content.Server.NameIdentifier;
using Content.Server.Stack;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Medical; // Frontier
using Content.Shared.Access.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Coordinates;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.NameIdentifier;
using Content.Shared.Paper;
using Content.Shared.Stacks;
using Content.Shared.Whitelist;
using JetBrains.Annotations;
using Robust.Server.Containers;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    [Dependency] private ContainerSystem _container = default!;
    [Dependency] private NameIdentifierSystem _nameIdentifier = default!;

    private static readonly ProtoId<NameIdentifierGroupPrototype> BountyNameIdentifierGroup = "Bounty";

    private EntityQuery<StackComponent> _stackQuery;
    private EntityQuery<ContainerManagerComponent> _containerQuery;
    private EntityQuery<CargoBountyLabelComponent> _bountyLabelQuery;

    private void InitializeBounty()
    {
        SubscribeLocalEvent<CargoBountyConsoleComponent, BoundUIOpenedEvent>(OnBountyConsoleOpened);
        SubscribeLocalEvent<CargoBountyConsoleComponent, BountyPrintLabelMessage>(OnPrintLabelMessage);
        SubscribeLocalEvent<CargoBountyConsoleComponent, BountySkipMessage>(OnSkipBountyMessage);
        // SubscribeLocalEvent<CargoBountyLabelComponent, PriceCalculationEvent>(OnGetBountyPrice); // Mono - Changed how bounty payout works. Not added to payout anymore.
        SubscribeLocalEvent<StationCargoBountyDatabaseComponent, MapInitEvent>(OnMapInit);

        _stackQuery = GetEntityQuery<StackComponent>();
        _containerQuery = GetEntityQuery<ContainerManagerComponent>();
        _bountyLabelQuery = GetEntityQuery<CargoBountyLabelComponent>();
    }

    private void OnBountyConsoleOpened(EntityUid uid, CargoBountyConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (_station.GetOwningStation(uid) is not { } station ||
            !TryComp<StationCargoBountyDatabaseComponent>(station, out var bountyDb))
            return;

        var untilNextSkip = bountyDb.NextSkipTime - _timing.CurTime;
        _uiSystem.SetUiState(uid, CargoConsoleUiKey.Bounty, new CargoBountyConsoleState(bountyDb.Bounties, bountyDb.History, untilNextSkip));
    }

    private void OnPrintLabelMessage(EntityUid uid, CargoBountyConsoleComponent component, BountyPrintLabelMessage args)
    {
        if (_timing.CurTime < component.NextPrintTime)
            return;

        if (_station.GetOwningStation(uid) is not { } station)
            return;

        if (!TryGetBountyFromId(station, args.BountyId, out var bounty))
            return;

        var label = Spawn(component.BountyLabelId, Transform(uid).Coordinates);
        component.NextPrintTime = _timing.CurTime + component.PrintDelay;
        SetupBountyLabel(label, station, bounty.Value);
        _audio.PlayPvs(component.PrintSound, uid);
    }

    private void OnSkipBountyMessage(EntityUid uid, CargoBountyConsoleComponent component, BountySkipMessage args)
    {
        if (_station.GetOwningStation(uid) is not { } station || !TryComp<StationCargoBountyDatabaseComponent>(station, out var db))
            return;

        if (_timing.CurTime < db.NextSkipTime)
            return;

        if (!TryGetBountyFromId(station, args.BountyId, out var bounty))
            return;

        if (args.Actor is not { Valid: true } mob)
            return;

        if (TryComp<AccessReaderComponent>(uid, out var accessReaderComponent) &&
            !_accessReaderSystem.IsAllowed(mob, uid, accessReaderComponent))
        {
            _audio.PlayPvs(component.DenySound, uid);
            return;
        }

        if (!TryRemoveBounty(station, bounty.Value, true, args.Actor))
            return;

        FillBountyDatabase(station);
        db.NextSkipTime = _timing.CurTime + db.SkipDelay;
        var untilNextSkip = db.NextSkipTime - _timing.CurTime;
        _uiSystem.SetUiState(uid, CargoConsoleUiKey.Bounty, new CargoBountyConsoleState(db.Bounties, db.History, untilNextSkip));
        _audio.PlayPvs(component.SkipSound, uid);
    }

    public void SetupBountyLabel(EntityUid uid, EntityUid stationId, CargoBountyData bounty, PaperComponent? paper = null, CargoBountyLabelComponent? label = null)
    {
        if (!Resolve(uid, ref paper, ref label) || !_protoMan.TryIndex<CargoBountyPrototype>(bounty.Bounty, out var prototype))
            return;

        label.Id = bounty.Id;
        label.AssociatedStationId = stationId;
        var msg = new FormattedMessage();
        msg.AddText(Loc.GetString("bounty-manifest-header", ("id", bounty.Id)));
        msg.PushNewline();
        msg.AddText(Loc.GetString("bounty-manifest-list-start"));
        msg.PushNewline();
        foreach (var entry in prototype.Entries)
        {
            msg.AddMarkupOrThrow($"- {Loc.GetString("bounty-console-manifest-entry",
                ("amount", entry.Amount),
                ("item", Loc.GetString(entry.Name)))}");
            msg.PushNewline();
        }
        msg.AddMarkupOrThrow(Loc.GetString("bounty-console-manifest-reward", ("reward", EffectiveBountyReward(bounty, prototype) + " (" + (bounty.FundedReward >= 0 ? "spesso" : prototype.RewardSuffix) + ")"))); // Mono - add suffix
        _paperSystem.SetContent((uid, paper), msg.ToMarkup());
    }

    /// <summary>
    /// calculated after it is sold separately from the selling system.
    /// </summary>
    private void OnGetBountyPrice(EntityUid uid, CargoBountyLabelComponent component, ref PriceCalculationEvent args)
    {
        if (args.Handled || component.Calculating)
            return;

        // make sure this label was actually applied to a crate.
        if (!_container.TryGetContainingContainer((uid, null, null), out var container) || container.ID != LabelSystem.ContainerName)
            return;

        if (component.AssociatedStationId is not { } station || !TryComp<StationCargoBountyDatabaseComponent>(station, out var database))
            return;

        if (database.CheckedBounties.Contains(component.Id))
            return;

        if (!TryGetBountyFromId(station, component.Id, out var bounty, database))
            return;

        if (!_protoMan.TryIndex(bounty.Value.Bounty, out var bountyPrototype) ||
            !IsBountyComplete(container.Owner, bountyPrototype))
            return;

        database.CheckedBounties.Add(component.Id);
        args.Handled = true;

        component.Calculating = true;
        args.Price = bountyPrototype.Reward - _pricing.GetPrice(container.Owner);
        component.Calculating = false;
    }

    internal void NotifyEntitiesSold(HashSet<EntityUid> soldEntities, EntityUid grid)
    {
        var marketGoods = new HashSet<EntityUid>();
        foreach (var sold in soldEntities)
        {
            if (!TryFulfillSoldBounty(sold))
                marketGoods.Add(sold);
        }

        var sale = new EntitySoldEvent(marketGoods, grid);
        RaiseLocalEvent(ref sale);
    }

    private bool TryFulfillSoldBounty(EntityUid sold)
    {
        if (!TryGetBountyLabel(sold, out _, out var label) ||
            label.AssociatedStationId is not { } station ||
            !TryGetBountyFromId(station, label.Id, out var bounty) ||
            !_protoMan.TryIndex(bounty.Value.Bounty, out var prototype) ||
            !IsBountyComplete(sold, prototype) ||
            !TryRemoveBounty(station, bounty.Value, false))
        {
            return false;
        }

        var reward = EffectiveBountyReward(bounty.Value, prototype);
        var rewardProto = bounty.Value.FundedReward >= 0 ? "SpaceCash" : prototype.RewardProto.Id;
        if (reward > 0)
        {
            if (!_economyCreditMode || bounty.Value.FundedReward < 0 && !IsCreditReward(prototype))
                _stack.SpawnMultiple(rewardProto, reward, sold.ToCoordinates());
            _procurement.RecordBounty(_procurementPallet, bounty.Value.Bounty.Id, rewardProto, reward);
        }

        FillBountyDatabase(station);
        _adminLogger.Add(LogType.Action, LogImpact.Low, $"Bounty \"{bounty.Value.Bounty}\" (id:{bounty.Value.Id}) was fulfilled");
        return true;
    }

    private bool TryGetBountyLabel(EntityUid uid,
        [NotNullWhen(true)] out EntityUid? labelEnt,
        [NotNullWhen(true)] out CargoBountyLabelComponent? labelComp)
    {
        labelEnt = null;
        labelComp = null;
        if (!_containerQuery.TryGetComponent(uid, out var containerMan))
            return false;

        // make sure this label was actually applied to a crate.
        if (!_container.TryGetContainer(uid, LabelSystem.ContainerName, out var container, containerMan))
            return false;

        if (container.ContainedEntities.FirstOrNull() is not { } label ||
            !_bountyLabelQuery.TryGetComponent(label, out var component))
            return false;

        labelEnt = label;
        labelComp = component;
        return true;
    }

    private void OnMapInit(EntityUid uid, StationCargoBountyDatabaseComponent component, MapInitEvent args)
    {
        FillBountyDatabase(uid, component);
    }

    /// <summary>
    /// Fills up the bounty database with random bounties.
    /// </summary>
    public void FillBountyDatabase(EntityUid uid, StationCargoBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        while (component.Bounties.Count < component.MaxBounties)
        {
            if (!TryAddBounty(uid, component))
                break;
        }

        UpdateBountyConsoles();
    }

    public void RerollBountyDatabase(Entity<StationCargoBountyDatabaseComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp))
            return;

        foreach (var pending in entity.Comp.Bounties)
            if (pending.FundedReward >= 0) EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().RefundFood(pending.FoodAccount, pending.FundedReward);
        entity.Comp.Bounties.Clear();
        FillBountyDatabase(entity);
    }

    public bool IsBountyComplete(EntityUid container, out HashSet<EntityUid> bountyEntities)
    {
        if (!TryGetBountyLabel(container, out _, out var component))
        {
            bountyEntities = new();
            return false;
        }

        var station = component.AssociatedStationId;
        if (station == null)
        {
            bountyEntities = new();
            return false;
        }

        if (!TryGetBountyFromId(station.Value, component.Id, out var bounty))
        {
            bountyEntities = new();
            return false;
        }

        return IsBountyComplete(container, bounty.Value, out bountyEntities);
    }

    public bool IsBountyComplete(EntityUid container, CargoBountyData data)
    {
        return IsBountyComplete(container, data, out _);
    }

    public bool IsBountyComplete(EntityUid container, CargoBountyData data, out HashSet<EntityUid> bountyEntities)
    {
        if (!_protoMan.TryIndex(data.Bounty, out var proto))
        {
            bountyEntities = new();
            return false;
        }

        return IsBountyComplete(container, proto.Entries, out bountyEntities);
    }

    public bool IsBountyComplete(EntityUid container, string id)
    {
        if (!_protoMan.TryIndex<CargoBountyPrototype>(id, out var proto))
            return false;

        return IsBountyComplete(container, proto.Entries);
    }

    public bool IsBountyComplete(EntityUid container, CargoBountyPrototype prototype)
    {
        return IsBountyComplete(container, prototype.Entries);
    }

    public bool IsBountyComplete(EntityUid container, IEnumerable<CargoBountyItemEntry> entries)
    {
        return IsBountyComplete(container, entries, out _);
    }

    public bool IsBountyComplete(EntityUid container, IEnumerable<CargoBountyItemEntry> entries, out HashSet<EntityUid> bountyEntities)
    {
        return IsBountyComplete(GetBountyEntities(container), entries, out bountyEntities);
    }

    /// <summary>
    /// Determines whether the <paramref name="entity"/> meets the criteria for the bounty <paramref name="entry"/>.
    /// </summary>
    /// <returns>true if <paramref name="entity"/> is a valid item for the bounty entry, otherwise false</returns>
    public bool IsValidBountyEntry(EntityUid entity, CargoBountyItemEntry entry)
    {
        if (!_whitelist.IsValid(entry.Whitelist, entity))
            return false;

        if (entry.Blacklist != null && _whitelist.IsValid(entry.Blacklist, entity))
            return false;

        if (entry.MinimumFoodNutrients > 0 && !HasMealNutrients(entity, entry.MinimumFoodNutrients))
            return false;
        return true;
    }

    public bool IsBountyComplete(HashSet<EntityUid> entities, IEnumerable<CargoBountyItemEntry> entries, out HashSet<EntityUid> bountyEntities)
    {
        bountyEntities = new();

        foreach (var entry in entries)
        {
            long count = 0;
            var cashEntry = false;

            // store entities that already satisfied an
            // entry so we don't double-count them.
            var temp = new HashSet<EntityUid>();
            foreach (var entity in entities)
            {
                if (!IsValidBountyEntry(entity, entry))
                    continue;

                var stack = _stackQuery.CompOrNull(entity);
                count += stack?.Count ?? 1;
                cashEntry |= stack?.StackTypeId == "Credit";
                temp.Add(entity);

                // Cash is a fixed-size exchange, not a commodity sale. Inspect every
                // matching stack so extra money, including split/nested stacks, is
                // rejected without deleting the player's surplus.
                if (count >= entry.Amount && !cashEntry)
                    break;
            }

            if (count < entry.Amount || cashEntry && count != entry.Amount)
                return false;

            foreach (var ent in temp)
            {
                entities.Remove(ent);
                bountyEntities.Add(ent);
            }
        }

        return true;
    }

    private HashSet<EntityUid> GetBountyEntities(EntityUid uid)
    {
        var entities = new HashSet<EntityUid>
        {
            uid
        };
        if (!TryComp<ContainerManagerComponent>(uid, out var containers))
            return entities;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                if (_bountyLabelQuery.HasComponent(ent))
                    continue;

                var children = GetBountyEntities(ent);
                foreach (var child in children)
                {
                    entities.Add(child);
                }
            }
        }

        return entities;
    }

    [PublicAPI]
    public bool TryAddBounty(EntityUid uid, StationCargoBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        // todo: consider making the cargo bounties weighted.
        // Mono - Switch it to taking from all protos to a list.
        var allBounties = new List<CargoBountyPrototype>();
        var filteredBounties = new List<CargoBountyPrototype>();
        foreach (var bountyId in component.AvailableBounties)
        {
            if (!_protoMan.TryIndex<CargoBountyPrototype>(bountyId, out var proto))
                break;
            allBounties.Add(proto);
        }
        if (EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().Enabled)
        {
            var foodSlots = Math.Min(2, component.MaxBounties);
            var foodCount = component.Bounties.Count(b => _protoMan.Index(b.Bounty).FoodProcurement);
            var nonFoodCount = component.Bounties.Count - foodCount;
            if (foodCount < foodSlots)
                foreach (var food in _protoMan.EnumeratePrototypes<CargoBountyPrototype>().Where(p => p.FoodProcurement))
                    if (!component.Bounties.Any(b => b.Bounty == food.ID) && TryAddBounty(uid, food, component)) return true;
            if (nonFoodCount >= component.MaxBounties - foodSlots) return false;
            allBounties.RemoveAll(p => p.FoodProcurement);
        }
        if (allBounties.Count == 0) return false;
        foreach (var proto in allBounties)
        {
            if (component.Bounties.Any(b => b.Bounty == proto.ID))
                continue;
            filteredBounties.Add(proto);
        }

        var pool = filteredBounties.Count == 0 ? allBounties : filteredBounties;
        var bounty = _random.Pick(pool);
        return TryAddBounty(uid, bounty, component);
    }

    [PublicAPI]
    public bool TryAddBounty(EntityUid uid, string bountyId, StationCargoBountyDatabaseComponent? component = null)
    {
        if (!_protoMan.TryIndex<CargoBountyPrototype>(bountyId, out var bounty))
        {
            return false;
        }

        return TryAddBounty(uid, bounty, component);
    }

    public bool TryAddBounty(EntityUid uid, CargoBountyPrototype bounty, StationCargoBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (component.Bounties.Count >= component.MaxBounties)
            return false;

        _nameIdentifier.GenerateUniqueName(uid, BountyNameIdentifierGroup, out var randomVal);
        var newBounty = new CargoBountyData(bounty, randomVal);
        // This bounty id already exists! Probably because NameIdentifierSystem ran out of ids.
        if (component.Bounties.Any(b => b.Id == newBounty.Id))
        {
            Log.Error("Failed to add bounty {ID} because another one with the same ID already existed!", newBounty.Id);
            return false;
        }
        if (bounty.FoodProcurement)
        {
            var economy = EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>();
            var reward = economy.MealReward * (long)bounty.Entries.Sum(e => e.Amount);
            if (reward is <= 0 or > int.MaxValue || !economy.TryFundFood(reward, out var payer)) return false;
            newBounty = newBounty with { FundedReward = (int)reward, FoodAccount = payer };
        }
        component.Bounties.Add(newBounty);
        _adminLogger.Add(LogType.Action, LogImpact.Low, $"Added bounty \"{bounty.ID}\" (id:{component.TotalBounties}) to station {ToPrettyString(uid)}");
        component.TotalBounties++;
        return true;
    }

    [PublicAPI]
    public bool TryRemoveBounty(Entity<StationCargoBountyDatabaseComponent?> ent,
        string dataId,
        bool skipped,
        EntityUid? actor = null)
    {
        if (!TryGetBountyFromId(ent.Owner, dataId, out var data, ent.Comp))
            return false;

        return TryRemoveBounty(ent, data.Value, skipped, actor);
    }

    public bool TryRemoveBounty(Entity<StationCargoBountyDatabaseComponent?> ent,
        CargoBountyData data,
        bool skipped,
        EntityUid? actor = null)
    {
        if (!Resolve(ent, ref ent.Comp))
            return false;

        if (_economyBounties.Contains((ent.Owner, data.Id)) && !_economyCreditMode) return false;
        for (var i = 0; i < ent.Comp.Bounties.Count; i++)
        {
            if (ent.Comp.Bounties[i].Id == data.Id)
            {
                string? actorName = null;
                if (actor != null)
                {
                    var getIdentityEvent = new TryGetIdentityShortInfoEvent(ent.Owner, actor.Value);
                    RaiseLocalEvent(getIdentityEvent);
                    actorName = getIdentityEvent.Title;
                }

                ent.Comp.History.Add(new CargoBountyHistoryData(data,
                    skipped
                        ? CargoBountyHistoryData.BountyResult.Skipped
                        : CargoBountyHistoryData.BountyResult.Completed,
                    _timing.CurTime,
                    actorName));
                if (skipped && data.FundedReward >= 0)
                    EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().RefundFood(data.FoodAccount, data.FundedReward);
                ent.Comp.Bounties.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public bool TryGetBountyFromId(
        EntityUid uid,
        string id,
        [NotNullWhen(true)] out CargoBountyData? bounty,
        StationCargoBountyDatabaseComponent? component = null)
    {
        bounty = null;
        if (!Resolve(uid, ref component))
            return false;

        foreach (var bountyData in component.Bounties)
        {
            if (bountyData.Id != id)
                continue;
            bounty = bountyData;
            break;
        }

        return bounty != null;
    }

    public void UpdateBountyConsoles()
    {
        var query = EntityQueryEnumerator<CargoBountyConsoleComponent, UserInterfaceComponent>();
        while (query.MoveNext(out var uid, out _, out var ui))
        {
            if (_station.GetOwningStation(uid) is not { } station ||
                !TryComp<StationCargoBountyDatabaseComponent>(station, out var db))
            {
                continue;
            }

            var untilNextSkip = db.NextSkipTime - _timing.CurTime;
            _uiSystem.SetUiState((uid, ui), CargoConsoleUiKey.Bounty, new CargoBountyConsoleState(db.Bounties, db.History, untilNextSkip));
        }
    }

    private void UpdateBounty()
    {
        var query = EntityQueryEnumerator<StationCargoBountyDatabaseComponent>();
        while (query.MoveNext(out var bountyDatabase))
        {
            bountyDatabase.CheckedBounties.Clear();
        }
    }
}
