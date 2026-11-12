using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
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

    private void TrySpawnCrate(EntityUid crateMachineUid,
        EntityUid player,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        BankAccountComponent playerBank)
    {
        if (!HasComp<MarketItemSpawnerComponent>(crateMachineUid) ||
            HasComp<MarketPendingShipmentComponent>(crateMachineUid) ||
            consoleComponent.CartDataList.Count == 0)
            return;

        var cartBalance = MarketDataExtensions.GetMarketValue(consoleComponent.CartDataList, marketMod);
        if (playerBank.Balance < cartBalance)
            return;

        // Withdraw spesos from player
        var spawnCost = int.Abs(MarketDataExtensions.GetMarketValue(consoleComponent.CartDataList, marketMod));
        if (!_bankSystem.TryBankWithdraw(player, spawnCost))
        {
            _popup.PopupEntity(Loc.GetString("market-insufficient-funds"), consoleUid, player);
            _audio.PlayPredicted(consoleComponent.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
            return;
        }
        _audio.PlayPredicted(consoleComponent.SuccessSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

        EnsureComp<MarketPendingShipmentComponent>(crateMachineUid).Items = consoleComponent.CartDataList;
        consoleComponent.CartDataList = [];
        _crateMachine.OpenFor(crateMachineUid, component);
        RefreshState(consoleUid, playerBank.Balance, marketMod, consoleComponent);
    }

    private void SpawnCrateItems(List<MarketLot> spawnList, EntityUid targetCrate)
    {
        var coordinates = Transform(targetCrate).Coordinates;
        foreach (var data in spawnList)
        {
            if (GetMarketStack(data) is { } stackPrototype)
            {
                var entityList = _stackSystem.SpawnMultiple(stackPrototype.Spawn, data.Quantity, coordinates);
                foreach (var entity in entityList)
                {
                    InsertPurchasedItem(entity, targetCrate, data.CraftedUnitLimit);
                }
            }
            else
            {
                // Spawn the requested quantity of non-stackable items
                for (int i = 0; i < data.Quantity; i++)
                {
                    var spawn = Spawn(data.Prototype, coordinates);
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
        SpawnCrateItems(items, targetCrate);
    }
}
