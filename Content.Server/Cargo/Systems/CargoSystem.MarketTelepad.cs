using System.Linq;
using Content.Server.Power.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Content.Shared.DeviceLinking;
using Robust.Shared.Audio;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    private readonly Dictionary<EntityUid, Guid> _marketPadReservations = new();
    private readonly Dictionary<EntityUid, TimeSpan> _marketPadCooldown = new();

    public bool HasMarketTelepad(EntityUid console) => MarketTelepads(console).Any();

    private IEnumerable<EntityUid> MarketTelepads(EntityUid console)
    {
        if (!Exists(console) || !TryComp<DeviceLinkSourceComponent>(console, out var source)) yield break;
        var targets = source.Outputs.GetValueOrDefault("OrderSender");
        if (targets == null) yield break;
        foreach (var pad in targets.OrderBy(uid => uid))
            if (IsMarketTelepad(console, pad)) yield return pad;
    }

    private bool IsMarketTelepad(EntityUid console, EntityUid pad)
    {
        return Exists(console) && Exists(pad) && !MetaData(console).EntityPaused && !MetaData(pad).EntityPaused && HasComp<CargoTelepadComponent>(pad) &&
            TryComp<ApcPowerReceiverComponent>(pad, out var power) && power.Powered &&
            Transform(pad).Anchored && Transform(pad).GridUid != null &&
            TryComp<DeviceLinkSourceComponent>(console, out var source) && source.Outputs.GetValueOrDefault("OrderSender")?.Contains(pad) == true;
    }

    public bool TryReserveMarketTelepad(EntityUid console, Guid shipment, out EntityUid pad)
    {
        if (shipment == Guid.Empty) { pad = default; return false; }
        foreach (var candidate in MarketTelepads(console))
        {
            if (_marketPadReservations.TryGetValue(candidate, out var existing))
            { if (existing == shipment) { pad = candidate; return true; } continue; }
            if (_marketPadCooldown.TryGetValue(candidate, out var until) && _timing.CurTime < until) continue;
            _marketPadReservations[candidate] = shipment;
            pad = candidate; return true;
        }
        pad = default; return false;
    }

    public bool ValidMarketTelepad(EntityUid console, EntityUid pad, Guid shipment)
        => _marketPadReservations.GetValueOrDefault(pad) == shipment && IsMarketTelepad(console, pad);

    public bool ValidMarketArrival(EntityUid pad, Guid shipment)
        => _marketPadReservations.GetValueOrDefault(pad) == shipment && Exists(pad) && !MetaData(pad).EntityPaused &&
           HasComp<CargoTelepadComponent>(pad) && Transform(pad).Anchored && Transform(pad).GridUid != null &&
           TryComp<ApcPowerReceiverComponent>(pad, out var power) && power.Powered;

    public void ReleaseMarketTelepad(EntityUid pad, Guid shipment, bool delivered)
    {
        if (_marketPadReservations.GetValueOrDefault(pad) != shipment) return;
        _marketPadReservations.Remove(pad);
        if (!delivered || !TryComp<CargoTelepadComponent>(pad, out var component)) return;
        component.CurrentState = CargoTelepadState.Teleporting;
        component.Accumulator = component.Delay;
        _marketPadCooldown[pad] = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(.1, component.Delay));
        _appearance.SetData(pad, CargoTelepadVisuals.State, CargoTelepadState.Teleporting);
        _audio.PlayPvs(component.TeleportSound, pad, AudioParams.Default.WithVolume(-8f));
    }

    private bool MarketTelepadBusy(EntityUid pad)
    {
        if (_marketPadReservations.ContainsKey(pad)) return true;
        if (!_marketPadCooldown.TryGetValue(pad, out var until)) return false;
        if (_timing.CurTime < until) return true;
        _marketPadCooldown.Remove(pad);
        if (TryComp<CargoTelepadComponent>(pad, out var component))
        {
            component.CurrentState = CargoTelepadState.Idle;
            component.Accumulator = 0;
            _appearance.SetData(pad, CargoTelepadVisuals.State, CargoTelepadState.Idle);
        }
        return false;
    }
}
