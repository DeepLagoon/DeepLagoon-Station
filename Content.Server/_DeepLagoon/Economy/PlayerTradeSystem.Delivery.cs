using System.Linq;
using Content.Server._NF.Market.Components;
using Content.Shared._DeepLagoon.Economy;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server._DeepLagoon.Economy;

public sealed partial class PlayerTradeSystem
{
    [Dependency] private SharedAudioSystem _tradeAudio = default!;
    private readonly Dictionary<Guid, Work> _automaticDeliveries = new();
    private readonly Queue<Guid> _deliveryQueue = new();
    private TimeSpan _nextDelivery;
    private bool UsesTelepad(EntityUid console) => TryComp<MarketConsoleComponent>(console, out var market) && market.TelepadDelivery;

    private void PurchaseSound(EntityUid console)
    {
        if (TryComp<MarketConsoleComponent>(console, out var market))
            _tradeAudio.PlayPvs(market.SuccessSound, console, AudioParams.Default.WithMaxDistance(5f));
    }
    private void TelepadErrorSound(EntityUid console)
    {
        if (TryComp<MarketConsoleComponent>(console, out var market))
            _tradeAudio.PlayPvs(market.ErrorSound, console, AudioParams.Default.WithMaxDistance(5f));
    }
    private bool ReserveDeliveryPad(Work work)
    {
        // Once the durable issuance committed, finish at the already reserved pad even if the console/link disappeared.
        if (work.Committed && work.ReceivingPad is { } issuedPad && Exists(issuedPad))
            return _cargo.ValidMarketArrival(issuedPad, work.Lease);
        if (work.ReceivingPad is { } current && _cargo.ValidMarketTelepad(work.Access.Console, current, work.Lease)) return true;
        ReleaseDeliveryPad(work, false);
        if (!_cargo.TryReserveMarketTelepad(work.Access.Console, work.Lease, out var pad)) return false;
        work.ReceivingPad = pad; return true;
    }
    private void ReleaseDeliveryPad(Work work, bool delivered)
    {
        if (work.ReceivingPad is not { } pad) return;
        _cargo.ReleaseMarketTelepad(pad, work.Lease, delivered);
        work.ReceivingPad = null;
    }
    private void QueueDeliveries(Work purchase)
    {
        foreach (var delivery in purchase.PurchasedDeliveries)
        {
            if (_automaticDeliveries.ContainsKey(delivery)) continue;
            var shipment = new Work { Session = purchase.Session, Access = purchase.Access, Delivery = delivery, Lease = Guid.NewGuid(),
                Automatic = true, ToTelepad = true, Query = purchase.Query, Apply = () => System.Threading.Tasks.Task.CompletedTask };
            shipment.Apply = () => Claim(shipment);
            _automaticDeliveries.Add(delivery, shipment); _deliveryQueue.Enqueue(delivery);
        }
        purchase.PurchasedDeliveries = [];
    }
    private void UpdateDeliveries()
    {
        if (_timing.RealTime < _nextDelivery) return;
        _nextDelivery = _timing.RealTime + TimeSpan.FromMilliseconds(100);
        // One deserialization per update, round-robin across pads; an unpowered order cannot block other consoles.
        var candidates = Math.Min(8, _deliveryQueue.Count);
        for (var i = 0; i < candidates && _deliveryQueue.TryDequeue(out var id); i++)
        {
            if (!_automaticDeliveries.TryGetValue(id, out var work)) continue;
            if (!Exists(work.Access.Console) && !work.Busy && !work.Committed)
            { Restore(work); Finish(work); continue; } // Unclaimed database deliveries remain available at another console.
            if (!Exists(work.Access.Console) && !work.Busy && work.Committed &&
                (work.ReceivingPad is not { } destination || !Exists(destination)))
            {
                // Both physical endpoints were destroyed after issuance. Record the same destruction for recovery.
                _recovery.AcceptExternalDelivery(work.Recovery, work.Entities);
                Restore(work); Finish(work); continue;
            }
            _deliveryQueue.Enqueue(id);
            if (work.Busy || work.ToTelepad && !ReserveDeliveryPad(work)) continue;
            _ = Process(work); break;
        }
    }
}
