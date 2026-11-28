using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
using System.Linq;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Content.Server._NF.Market.Extensions;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.Components;
using Content.Shared._NF.Market.Events;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Content.Shared._NF.CrateMachine.Components;

namespace Content.Server._NF.Market.Systems;

public sealed partial class MarketSystem
{
    [Dependency] private CrateMachineSystem _crateMachine = default!;

    private void InitializeCrateMachine()
    {
        SubscribeLocalEvent<MarketConsoleComponent, MarketPurchaseMessage>(OnMarketConsolePurchaseCrateMessage);
        SubscribeLocalEvent<CrateMachineComponent, CrateMachineOpenedEvent>(OnCrateMachineOpened);
    }

    private void OnMarketConsolePurchaseCrateMessage(EntityUid consoleUid,
        MarketConsoleComponent component,
        ref MarketPurchaseMessage args)
    {
        var marketMod = 1f;
        if (TryComp<MarketModifierComponent>(consoleUid, out var marketModComponent))
        {
            marketMod = marketModComponent.Mod;
        }
        if (component.TelepadDelivery)
        {
            var cargo = EntityManager.System<Content.Server.Cargo.Systems.CargoSystem>();
            var shipment = Guid.NewGuid();
            if (!cargo.TryReserveMarketTelepad(consoleUid, shipment, out var pad))
            {
                _popup.PopupEntity(Loc.GetString("market-no-telepad-available"), consoleUid, args.Actor);
                _audio.PlayPredicted(component.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
                if (TryComp<BankAccountComponent>(args.Actor, out var account)) RefreshState(consoleUid, account.Balance, marketMod, component);
                return;
            }
            var delivered = false;
            try
            {
                if (args.Actor is { Valid: true } actor && TryComp<BankAccountComponent>(actor, out var bank))
                    delivered = TrySpawnCrate(pad, actor, consoleUid, null, component, marketMod, bank, true);
            }
            finally { cargo.ReleaseMarketTelepad(pad, shipment, delivered); }
            return;
        }

        if (!_crateMachine.FindNearestUnoccupied(consoleUid, component.MaxCrateMachineDistance, out var machineUid) || !_entityManager.TryGetComponent<CrateMachineComponent> (machineUid, out var comp))
        {
            _popup.PopupEntity(Loc.GetString("market-no-crate-machine-available"), consoleUid, Filter.PvsExcept(consoleUid), true);
            _audio.PlayPredicted(component.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

            return;
        }
        OnPurchaseCrateMessage(machineUid.Value, consoleUid, comp, component, marketMod, args);
    }

    private void OnPurchaseCrateMessage(EntityUid crateMachineUid,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        MarketPurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (!TryComp<BankAccountComponent>(player, out var bankAccount))
            return;

        TrySpawnCrate(crateMachineUid, player, consoleUid, component, consoleComponent, marketMod, bankAccount);
    }

    private bool TrySpawnCrate(EntityUid crateMachineUid,
        EntityUid player,
        EntityUid consoleUid,
        CrateMachineComponent? component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        BankAccountComponent playerBank, bool telepad = false)
    {
        if (!telepad && (!HasComp<MarketItemSpawnerComponent>(crateMachineUid) ||
            HasComp<MarketPendingShipmentComponent>(crateMachineUid)) ||
            consoleComponent.CartDataList.Count == 0)
            return false;

        var cartBalance = checked(MarketDataExtensions.GetMarketValue(consoleComponent.CartDataList, marketMod) +
            (telepad ? consoleComponent.TransactionCost : 0));
        if (playerBank.Balance < cartBalance)
            return false;

        // Withdraw spesos from player
        var spawnCost = Math.Abs(cartBalance);
        var recovery = EntityManager.System<Content.Server._DeepLagoon.CrashRecovery.CrashRecoverySystem>();
        if (!recovery.CanProtectBuyer(player)) return false;
        var recoveryPrices = new List<long>();
        foreach (var lot in consoleComponent.CartDataList)
        {
            var stack = GetMarketStack(lot);
            var perStack = lot.Quantity;
            if (stack != null)
            {
                _prototypeManager.Index<EntityPrototype>(stack.Spawn).TryComp<StackComponent>(out var componentStack, EntityManager.ComponentFactory);
                perStack = _stackSystem.GetMaxCount(componentStack);
            }
            var count = stack != null ? (lot.Quantity - 1) / Math.Max(1, perStack) + 1 : lot.Quantity;
            var lotCost = checked((long)Math.Round(lot.Price * lot.Quantity * marketMod));
            for (var i = 0; i < count; i++) recoveryPrices.Add(lotCost / count + (i < lotCost % count ? 1 : 0));
        }
        if (telepad && recoveryPrices.Count > 0) recoveryPrices[0] += consoleComponent.TransactionCost;
        var recoveryPayment = recovery.BeginPurchase(player, spawnCost, recoveryPrices.Count, recoveryPrices.ToArray());
        if (!_bankSystem.TryBankWithdraw(player, spawnCost, recoveryPayment))
        {
            _popup.PopupEntity(Loc.GetString("market-insufficient-funds"), consoleUid, player);
            _audio.PlayPredicted(consoleComponent.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
            return false;
        }
        _audio.PlayPredicted(consoleComponent.SuccessSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

        if (telepad)
        {
            var key = new object();
            recovery.AttachPurchase(key, recoveryPayment);
            var crate = Spawn("CrateGenericSteel", Transform(crateMachineUid).Coordinates);
            SpawnCrateItems(consoleComponent.CartDataList, crate, key);
            consoleComponent.CartDataList = [];
            RefreshState(consoleUid, playerBank.Balance, marketMod, consoleComponent);
            return true;
        }

        var pending = EnsureComp<MarketPendingShipmentComponent>(crateMachineUid);
        pending.Items = consoleComponent.CartDataList;
        recovery.AttachPurchase(pending, recoveryPayment);
        consoleComponent.CartDataList = [];
        _crateMachine.OpenFor(crateMachineUid, component!);
        RefreshState(consoleUid, playerBank.Balance, marketMod, consoleComponent);
        return true;
    }

    private void SpawnCrateItems(List<MarketLot> spawnList, EntityUid targetCrate, object recoveryKey)
    {
        var coordinates = Transform(targetCrate).Coordinates;
        foreach (var data in spawnList)
        {
            if (GetMarketStack(data) is { } stackPrototype)
            {
                var entityList = _stackSystem.SpawnMultiple(stackPrototype.Spawn, data.Quantity, coordinates);
                foreach (var entity in entityList)
                {
                    EntityManager.System<Content.Server._DeepLagoon.CrashRecovery.CrashRecoverySystem>().BindPurchasedProduct(recoveryKey, entity);
                    InsertPurchasedItem(entity, targetCrate, data.CraftedUnitLimit);
                }
            }
            else
            {
                // Spawn the requested quantity of non-stackable items
                for (int i = 0; i < data.Quantity; i++)
                {
                    var spawn = Spawn(data.Prototype, coordinates);
                    EntityManager.System<Content.Server._DeepLagoon.CrashRecovery.CrashRecoverySystem>().BindPurchasedProduct(recoveryKey, spawn);
                    InsertPurchasedItem(spawn, targetCrate, data.CraftedUnitLimit);
                }
            }
        }
    }

    private void InsertPurchasedItem(EntityUid item, EntityUid crate, double? unitLimit)
    {
        if (unitLimit is { } limit)
            _pricingSystem.MarkCraftedProduct(item, limit);

        _crateMachine.InsertIntoCrate(item, crate);
    }

    private void OnCrateMachineOpened(EntityUid uid, CrateMachineComponent component, CrateMachineOpenedEvent args)
    {
        if (!TryComp<MarketPendingShipmentComponent>(uid, out var shipment))
            return;

        var items = shipment.Items;
        RemComp<MarketPendingShipmentComponent>(uid);
        var targetCrate = _crateMachine.SpawnCrate(uid, component);
        SpawnCrateItems(items, targetCrate, shipment);
    }
}
