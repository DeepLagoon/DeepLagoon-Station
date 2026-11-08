using System.Linq;
using Content.Server._NF.GameRule;
using Content.Server.Cargo.Systems;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private PricingSystem _pricing = default!;
    private TimeSpan _nextPayoutRetry;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.RealTime < _nextPayoutRetry)
            return;

        _nextPayoutRetry = _timing.RealTime + TimeSpan.FromMinutes(1);
        PayPendingSales();
    }

    public void SellParkedShuttles()
    {
        foreach (var grid in _parkingMaps.Keys.ToArray())
        {
            if (TerminatingOrDeleted(grid) || !TryComp<ParkingShuttleComponent>(grid, out var ship) || !ship.Parked)
                continue;

            try
            {
                var price = _shipyard.AppraiseParkedShuttle(grid);
                if (price > 0)
                    _store.QueuePayout(new ParkingPayout(ship.Identity, ship.OwnerAccount, ship.ParkingCharacter, price));

                RemoveParkedShip(grid, ship);
                Log.Info($"Sold parked shuttle {ship.Identity} for {price} credits to {ship.OwnerAccount}.");
            }
            catch (Exception e)
            {
                Log.Error($"Failed to sell parked shuttle {ship.Identity}: {e}");
            }
        }

        PayPendingSales();
    }

    private void PayPendingSales()
    {
        foreach (var payout in _store.PendingPayouts())
            PaySale(payout);
    }

    private bool PaySale(ParkingPayout payout)
    {
        var user = new NetUserId(payout.Owner);
        try
        {
            if (_bank.CreditParkingSale(user, payout.Character, payout.Amount, payout.Id) is not { } credit)
                return false;

            EntityManager.System<NFAdventureRuleSystem>().RecordParkingBalance(user, credit.Character, credit.Balance);
            _store.FinishPayout(payout.Id);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Parking payout {payout.Id} for {user} is pending: {e}");
            return false;
        }
    }
}
