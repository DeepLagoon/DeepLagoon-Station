using Content.Server._DeepLagoon.Currency;
using Content.Server.Power.EntitySystems;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.VendingMachines;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.VendingMachines;

/// <summary>Explicit per-product LC catalogue. Prices for every other product use the ordinary vendor economy.</summary>
[RegisterComponent]
public sealed partial class LagoonCoinVendingCatalogComponent : Component
{
    [DataField] public Dictionary<string, int> Prices = new();
}

public sealed partial class VendingMachineSystem
{
    [Dependency] private LagoonCoinSystem _lagoonCoins = default!;
    [Dependency] private SharedInteractionSystem _lagoonInteraction = default!;
    private readonly HashSet<EntityUid> _lagoonVendBusy = new();
    private readonly HashSet<NetUserId> _lagoonBuyerBusy = new();

    private bool TryGetLagoonCoinPrice(EntityUid vendor, string item, out int price)
    {
        price = 0;
        return TryComp<LagoonCoinVendingCatalogComponent>(vendor, out var catalogue) &&
            catalogue.Prices.TryGetValue(item, out price) && price > 0;
    }

    private async void VendForLagoonCoins(EntityUid vendor, EntityUid buyer, InventoryType type, string item,
        int price, VendingMachineComponent component)
    {
        if (!TryComp<ActorComponent>(buyer, out var actor) || !_lagoonVendBusy.Add(vendor)) return;
        var session = actor.PlayerSession;
        var user = session.UserId;
        if (!_lagoonBuyerBusy.Add(user)) { _lagoonVendBusy.Remove(vendor); return; }
        var order = $"vendor:{Guid.NewGuid():N}";
        var paid = false;
        var debitStarted = false;
        var delivered = false;
        try
        {
            bool CanDeliver() => !TerminatingOrDeleted(vendor) && !TerminatingOrDeleted(buyer) &&
                session.AttachedEntity == buyer && !component.Broken && !component.Ejecting &&
                this.IsPowered(vendor, EntityManager) && _lagoonInteraction.InRangeUnobstructed(buyer, vendor) &&
                IsAuthorized(vendor, buyer, component) && GetEntry(vendor, item, type, component) is { Amount: > 0 };
            if (!CanDeliver()) return;
            debitStarted = true;
            await _lagoonCoins.Spend(user, price, order, "Набор экспедиционного улучшения");
            paid = true;
            if (!CanDeliver()) throw new InvalidOperationException("Vending access changed before delivery.");
            var product = Spawn(item, Transform(vendor).Coordinates);
            delivered = true; // Later audiovisual failure must never refund an already delivered item.
            var entry = GetEntry(vendor, item, type, component)!;
            if (entry.Amount != uint.MaxValue) entry.Amount--;
            component.Ejecting = true;
            component.EjectAccumulator = 0;
            component.NextItemToEject = null;
            component.ThrowNextItem = false;
            component.LastPurchasePrice = null;
            Dirty(vendor, component);
            TryUpdateVisualState(vendor, component);
            Audio.PlayPvs(component.SoundVend, vendor);
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"{ToPrettyString(buyer):user} bought {ToPrettyString(product):entity} for {price} LC from {ToPrettyString(vendor):entity}; operation={order}.");
        }
        catch (Content.Server.Database.InsufficientLagoonCoinsException)
        {
            if (!TerminatingOrDeleted(vendor))
            {
                _popupSystem.PopupEntity(Loc.GetString("expedition-upgrade-lc-insufficient"), vendor, buyer);
                Deny(vendor, component);
            }
        }
        catch (Exception error)
        {
            if (!delivered && debitStarted)
            {
                try
                {
                    // Resolve an ambiguous debit response using the SAME receipt before refunding.
                    if (!paid) await _lagoonCoins.Spend(user, price, order, "Набор экспедиционного улучшения");
                    await _lagoonCoins.RefundVendingSpend(user, price, order);
                }
                catch (Content.Server.Database.InsufficientLagoonCoinsException) { }
                catch (Exception refundError) { Log.Error($"LC vendor refund requires reconciliation: user={user}, operation={order}, amount={price}: {refundError}"); }
            }
            Log.Error($"LC vending operation {order} failed; paid={paid}, delivered={delivered}: {error}");
            if (!TerminatingOrDeleted(vendor)) Deny(vendor, component);
        }
        finally
        {
            _lagoonVendBusy.Remove(vendor);
            _lagoonBuyerBusy.Remove(user);
        }
    }
}
