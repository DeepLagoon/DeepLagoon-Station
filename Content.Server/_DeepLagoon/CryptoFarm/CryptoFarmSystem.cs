using System.Linq;
using Content.Server._DeepLagoon.Exchange;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Containers;

namespace Content.Server._DeepLagoon.CryptoFarm;

public sealed partial class CryptoFarmSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<CryptoFarmComponent, ItemMinerCheckEvent>(OnCheck);
        SubscribeLocalEvent<CryptoFarmComponent, ItemMinerProduceEvent>(OnProduce);
        SubscribeLocalEvent<CryptoFarmComponent, ContainerIsInsertingAttemptEvent>(OnInsertAttempt);
        SubscribeLocalEvent<CryptoFarmComponent, ComponentStartup>(OnMiningStartup);
        SubscribeLocalEvent<CryptoFarmComponent, EntityUnpausedEvent>(OnMiningUnpaused);
        InitializeWalletBinding();
    }

    private void OnMiningStartup(EntityUid uid, CryptoFarmComponent farm, ref ComponentStartup args)
    {
        if (farm.HasPendingMining) EntityManager.System<ExchangeSystem>().QueueMint(uid);
    }

    private void OnMiningUnpaused(EntityUid uid, CryptoFarmComponent farm, ref EntityUnpausedEvent args)
    {
        if (farm.HasPendingMining) EntityManager.System<ExchangeSystem>().QueueMint(uid);
    }

    public int StoredCoins(EntityUid uid)
    {
        if (!TryComp<StorageComponent>(uid, out var storage) || storage.Container == null)
            return 0;
        return storage.Container.ContainedEntities.Where(entity => !EntityManager.IsQueuedForDeletion(entity))
            .Sum(entity => TryComp<StackComponent>(entity, out var stack) ? stack.Count : 1);
    }

    private void OnCheck(EntityUid uid, CryptoFarmComponent farm, ref ItemMinerCheckEvent args)
    {
        var ready = EntityManager.System<ExchangeSystem>().Enabled && farm.IsBound &&
            !farm.HasPendingMining && !_binding.Contains(uid);
        if (TryComp<ItemMinerComponent>(uid, out var miner))
            miner.PendingPayouts = ready ? Math.Min(miner.PendingPayouts, 1) : 0;
        if (!ready)
            args.Cancelled = true;
    }

    private void OnProduce(EntityUid uid, CryptoFarmComponent farm, ref ItemMinerProduceEvent args)
    {
        args.Handled = true;
        var exchange = EntityManager.System<ExchangeSystem>();
        if (!exchange.Enabled || !farm.IsBound || farm.HasPendingMining || _binding.Contains(uid)) return;
        farm.MiningOperation = Guid.NewGuid();
        farm.MiningUser = farm.WalletUser;
        farm.MiningProfileId = farm.WalletProfileId;
        exchange.QueueMint(uid);
    }

    private TimeSpan _nextMiningRetry;
    public override void Update(float frameTime)
    {
        if (_timing.RealTime < _nextMiningRetry) return;
        _nextMiningRetry = _timing.RealTime + TimeSpan.FromSeconds(5);
        var exchange = EntityManager.System<ExchangeSystem>();
        // Queries omit paused maps. Restored farms resume an existing receipt, never a new issuance request.
        var query = EntityQueryEnumerator<CryptoFarmComponent>();
        while (query.MoveNext(out var uid, out var farm))
        {
            RetireLegacyStorage(uid);
            if (exchange.Enabled && farm.HasPendingMining) exchange.QueueMint(uid);
        }
    }

    /// <summary>Old parking snapshots may still contain the obsolete coin buffer. Keep its contents on the grid.</summary>
    public void RetireLegacyStorage(EntityUid uid)
    {
        if (!HasComp<CryptoFarmComponent>(uid)) return;
        if (_containers.TryGetContainer(uid, StorageComponent.ContainerId, out var container))
        {
            // Run after entity startup/deserialization, so all saved children have been restored first.
            _containers.EmptyContainer(container, force: true, destination: Transform(uid).Coordinates);
            if (container.ContainedEntities.Count > 0) return;
            RemComp<StorageComponent>(uid);
            _containers.ShutdownContainer(container);
        }
        else
            RemComp<StorageComponent>(uid);
        RemComp<UserInterfaceComponent>(uid);
    }

    private void OnInsertAttempt(EntityUid uid, CryptoFarmComponent farm, ContainerIsInsertingAttemptEvent args)
    {
        if (args.Cancelled || args.Container.ID != StorageComponent.ContainerId)
            return;
        var incoming = TryComp<StackComponent>(args.EntityUid, out var stack) ? stack.Count : 1;
        if (StoredCoins(uid) + incoming > farm.Capacity)
            args.Cancel();
    }

    /// <summary>Also protects direct merges into a coin already inside the machine.</summary>
    internal bool CanMergeInto(EntityUid donor, EntityUid recipient)
    {
        if (!_containers.TryGetContainingContainer((recipient, null, null), out var target)
            || target.ID != StorageComponent.ContainerId || !TryComp<CryptoFarmComponent>(target.Owner, out var farm))
            return true;
        if (_containers.TryGetContainingContainer((donor, null, null), out var source) && source == target)
            return true;
        var incoming = TryComp<StackComponent>(donor, out var stack) ? stack.Count : 1;
        return StoredCoins(target.Owner) + incoming <= farm.Capacity;
    }
}
