using Content.Server._Goobstation.ItemMiner;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Mining;

public sealed class LaserDrillSystem : EntitySystem
{
    [Dependency] private SharedMaterialStorageSystem _materials = default!;
    [Dependency] private SharedOreSiloSystem _silos = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private float _uiAccumulator;
    private readonly Dictionary<EntityUid, int> _gridDrillCounts = new();

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(ItemMinerSystem));
        SubscribeLocalEvent<LaserDrillComponent, ItemMinerCheckEvent>(OnCheck);
        SubscribeLocalEvent<LaserDrillComponent, ItemMinerProduceEvent>(OnProduce);
        Subs.BuiEvents<LaserDrillComponent>(LaserDrillUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnOpened);
            subs.Event<LaserDrillSelectMessage>(OnSelect);
        });
    }

    private void OnSelect(Entity<LaserDrillComponent> ent, ref LaserDrillSelectMessage args)
    {
        if (!ent.Comp.Materials.TryGetValue(args.Material, out var amount) || amount <= 0
            || ent.Comp.SelectedMaterial == args.Material)
            return;

        ent.Comp.SelectedMaterial = args.Material;
        // A newly selected resource must earn a full cycle, including when production was queued.
        if (TryComp<ItemMinerComponent>(ent, out var miner))
        {
            UpdateInterval(ent, miner);
            miner.PendingPayouts = 0;
            miner.NextAt = _timing.CurTime + miner.Interval;
        }
        UpdateUi(ent);
    }

    private void OnOpened(Entity<LaserDrillComponent> ent, ref BoundUIOpenedEvent args) => UpdateUi(ent);

    private void OnCheck(Entity<LaserDrillComponent> ent, ref ItemMinerCheckEvent args)
    {
        if (TryComp<ItemMinerComponent>(ent, out var miner))
            UpdateInterval(ent, miner);
        args.Cancelled |= GetStorageStatus(ent, out _) != LaserDrillStatus.Mining;
    }

    private void UpdateInterval(Entity<LaserDrillComponent> ent, ItemMinerComponent miner)
    {
        if (ent.Comp.SelectedMaterial is not { } material
            || !ent.Comp.Materials.ContainsKey(material)
            || MetaData(ent).EntityPrototype is not { } prototype
            || !prototype.TryGetComponent<ItemMinerComponent>(out var baseMiner, EntityManager.ComponentFactory))
            return;

        // Compare prices per unit of volume, independently of the amount paid out per cycle.
        // Always use the prototype interval so switching resources or loading a saved drill cannot compound it.
        var multiplier = _prototypes.Index(material).Price / _prototypes.Index<MaterialPrototype>("RawIron").Price;
        var interval = TimeSpan.FromSeconds(baseMiner.Interval.TotalSeconds * multiplier);
        if (miner.Interval == interval)
            return;

        miner.Interval = interval;
        miner.PendingPayouts = 0;
        miner.NextAt = _timing.CurTime + interval;
    }

    private void UpdatePowerConsumption()
    {
        _gridDrillCounts.Clear();
        var query = EntityQueryEnumerator<LaserDrillComponent, PowerConsumerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out _, out var xform))
        {
            if (!xform.Anchored || xform.GridUid is not { } grid
                || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
                continue;

            _gridDrillCounts[grid] = _gridDrillCounts.GetValueOrDefault(grid) + 1;
        }

        query = EntityQueryEnumerator<LaserDrillComponent, PowerConsumerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var consumer, out var xform))
        {
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid)
                || MetaData(uid).EntityPrototype is not { } prototype
                || !prototype.TryGetComponent<PowerConsumerComponent>(out var baseConsumer, EntityManager.ComponentFactory))
                continue;

            var count = xform.Anchored && xform.GridUid is { } grid
                ? _gridDrillCounts.GetValueOrDefault(grid) : 1;
            // Each additional anchored drill doubles every drill's demand on this grid.
            // Clamp to a finite load even for an excessively large number of drills.
            consumer.DrawRate = (float) Math.Min(float.MaxValue,
                baseConsumer.DrawRate * Math.Pow(2, Math.Max(0, count - 1)));
        }
    }

    private void OnProduce(Entity<LaserDrillComponent> ent, ref ItemMinerProduceEvent args)
    {
        // Always suppress the ordinary item/spawner output, even if the link has become invalid.
        args.Handled = true;
        if (GetStorageStatus(ent, out var silo) != LaserDrillStatus.Mining
            || ent.Comp.SelectedMaterial is not { } material)
            return;

        _materials.TryChangeMaterialAmount(silo, material, ent.Comp.Materials[material], localOnly: true);
    }

    private LaserDrillStatus GetStorageStatus(Entity<LaserDrillComponent> ent, out EntityUid silo)
    {
        silo = default;
        if (ent.Comp.SelectedMaterial is not { } material
            || !ent.Comp.Materials.TryGetValue(material, out var amount) || amount <= 0)
            return LaserDrillStatus.NoResource;

        if (!TryComp<OreSiloClientComponent>(ent, out var client) || client.Silo is not { } linked)
            return LaserDrillStatus.NoSilo;

        silo = linked;
        if (TerminatingOrDeleted(silo) || EntityManager.IsQueuedForDeletion(silo)
            || !_silos.IsLinkedTo(silo, ent)
            || !HasComp<MaterialStorageComponent>(silo) || !_silos.CanTransmitMaterials(silo, ent))
            return LaserDrillStatus.SiloUnavailable;

        return _materials.CanChangeMaterialAmount(silo, material, amount, localOnly: true)
            ? LaserDrillStatus.Mining : LaserDrillStatus.StorageFull;
    }

    private void UpdateUi(Entity<LaserDrillComponent> ent)
    {
        if (!_ui.IsUiOpen(ent.Owner, LaserDrillUiKey.Key))
            return;

        var status = GetStorageStatus(ent, out _);
        if (status == LaserDrillStatus.Mining && TryComp<ItemMinerComponent>(ent, out var miner))
        {
            var check = new ItemMinerCheckEvent(miner.Proto);
            RaiseLocalEvent(ent, ref check);
            if (check.Cancelled || miner.NeedsAnchored && !Transform(ent).Anchored)
                status = LaserDrillStatus.Disabled;
        }
        var siloName = TryComp<OreSiloClientComponent>(ent, out var client)
            && client.Silo is { } linked && !TerminatingOrDeleted(linked) ? Name(linked) : string.Empty;
        _ui.SetUiState(ent.Owner, LaserDrillUiKey.Key,
            new LaserDrillState(new(ent.Comp.Materials), ent.Comp.SelectedMaterial, siloName, status));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdatePowerConsumption();
        _uiAccumulator += frameTime;
        if (_uiAccumulator < 1f)
            return;
        _uiAccumulator %= 1f;
        var query = EntityQueryEnumerator<LaserDrillComponent>();
        while (query.MoveNext(out var uid, out var drill))
            UpdateUi((uid, drill));
    }
}
