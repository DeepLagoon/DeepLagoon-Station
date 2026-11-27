using System.Linq;
using Content.Server._NF.GameRule;
using Content.Server.Cargo.Systems;
using Content.Server.GameTicking;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private PricingSystem _pricing = default!;
    private TimeSpan _nextPayoutRetry;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdateSaveWork();
        UpdateFreeParking();
        UpdateStoredVoucherSales();
        UpdateShipyardRepairs();
        if (_ticker.RunLevel == GameRunLevel.InRound)
            UpdateAutoParking(_timing.CurTime);
        else if (_autoParking.Count > 0)
            ClearAutoParking();

        if (_timing.RealTime < _nextPayoutRetry)
            return;

        _nextPayoutRetry = _timing.RealTime + TimeSpan.FromMinutes(1);
        PayPendingSales();
    }

    public void SellParkedShuttles()
    {
        DrainSaveWork();
        StopFreeParking(invalidateCurrent: true);
        foreach (var grid in _parkingMaps.Keys.ToArray())
        {
            if (TerminatingOrDeleted(grid) || !TryComp<ParkingShuttleComponent>(grid, out var ship) || !ship.Parked)
                continue;

            try
            {
                var price = _shipyard.AppraiseParkedShuttle(grid);
                if (price > 0)
                {
                    var payout = new ParkingPayout(ship.Identity, ship.OwnerAccount, ship.ParkingCharacter, price);
                    if (EntityManager.System<Content.Server._DeepLagoon.Economy.SectorEconomySystem>().Enabled) payout = PrepareEconomyPayout(payout);
                    _store.QueuePayout(payout);
                }

                RemoveParkedShip(grid, ship);
                Log.Info($"Sold parked shuttle {ship.Identity} for {price} credits to {ship.OwnerAccount}.");
            }
            catch (Exception e)
            {
                Log.Error($"Failed to sell parked shuttle {ship.Identity}: {e}");
            }
        }

        QueueRecoveredParkingRoundEndSales();
        PayPendingSales();
    }

    private void PayPendingSales()
    {
        if ((_saveWork?.Finish ?? _saveWork?.Commit) is { IsCompleted: false }) return;
        foreach (var payout in _store.PendingPayouts())
            PaySale(payout);
    }

    private bool PaySale(ParkingPayout payout)
    {
        if (payout.ProfileId > 0)
        {
            QueueEconomyPayout(payout);
            return false;
        }
        var user = new NetUserId(payout.Owner);
        try
        {
            if (_bank.CreditParkingSale(user, payout.Character, payout.Amount, payout.Id) is not { } credit)
                return false;

            EntityManager.System<NFAdventureRuleSystem>().RecordParkingBalance(user, credit.Character, credit.Balance);
            _store.FinishPayout(payout.Id);
            EntityManager.System<Content.Server._DeepLagoon.Procurement.ProcurementSystem>()
                .RecordParkingPayment(payout.Id, payout.Owner, payout.Character, payout.Amount, credit.Balance);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Parking payout {payout.Id} for {user} is pending: {e}");
            return false;
        }
    }
}
