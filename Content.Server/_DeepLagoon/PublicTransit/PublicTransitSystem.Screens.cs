using Content.Server._DeepLagoon.PublicTransit;
using Content.Server._NF.PublicTransit.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.CCVar;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.TextScreen;

namespace Content.Server._NF.PublicTransit;

public sealed partial class PublicTransitSystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    private TimeSpan _nextScreenUpdate;

    private void InitializeBusScreens()
    {
        SubscribeLocalEvent<BusScreenComponent, MapInitEvent>((uid, _, _) => UpdateBusScreen(uid, Transform(uid)));
    }

    private void UpdateBusScreens()
    {
        if (_timing.CurTime < _nextScreenUpdate)
            return;

        _nextScreenUpdate = _timing.CurTime + TimeSpan.FromSeconds(1);
        var query = EntityQueryEnumerator<BusScreenComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
            UpdateBusScreen(uid, transform);
    }

    private void UpdateBusScreen(EntityUid uid, TransformComponent transform)
    {
        var text = Loc.GetString("dl-bus-screen-no-service");
        var target = TimeSpan.Zero;
        if (transform.GridUid is { } grid && TryGetBusArrival(grid, out var arrival, out var arrived))
        {
            text = Loc.GetString(arrived ? "dl-bus-screen-arrived" : "dl-bus-screen-eta");
            if (!arrived)
                target = arrival;
        }

        _appearance.SetData(uid, TextScreenVisuals.DefaultText,
            target == TimeSpan.Zero ? text : Loc.GetString("dl-bus-screen-no-service"));
        _appearance.SetData(uid, TextScreenVisuals.ScreenText, text);
        _appearance.SetData(uid, TextScreenVisuals.TargetTime, target);
    }

    /// <summary>Finds the earliest arrival on the registered grid, including intermediate stops.</summary>
    public bool TryGetBusArrival(EntityUid grid, out TimeSpan arrival, out bool arrived)
    {
        arrival = TimeSpan.MaxValue;
        arrived = false;
        if (!Enabled || !StationList.Contains(grid) || Deleted(grid) || MetaData(grid).EntityPaused)
            return false;

        var now = _timing.CurTime;
        var flightTime = TimeSpan.FromSeconds(_shuttles.DefaultStartupTime + Math.Max(FlyTime, _shuttles.DefaultArrivalTime));
        var interval = TimeSpan.FromSeconds(FlyTime + _cfgManager.GetCVar(NFCCVars.PublicTransitWaitTime));
        var query = EntityQueryEnumerator<TransitShuttleComponent, ShuttleComponent>();
        while (query.MoveNext(out var uid, out var bus, out _))
        {
            var flying = TryComp<FTLComponent>(uid, out var ftl) &&
                ftl.State is FTLState.Available or FTLState.Starting or FTLState.Travelling or FTLState.Arriving;
            if (!flying && bus.CurrentStation == grid)
            {
                arrived = true;
                arrival = now;
                return true;
            }

            if (flying && bus.Destination == grid)
            {
                var end = ftl!.StateTime.End;
                end += ftl.State switch
                {
                    FTLState.Available or FTLState.Starting => TimeSpan.FromSeconds(Math.Max(ftl.TravelTime, _shuttles.DefaultArrivalTime)),
                    FTLState.Travelling => TimeSpan.FromSeconds(_shuttles.DefaultArrivalTime),
                    _ => TimeSpan.Zero,
                };
                if (end > now && end < arrival)
                    arrival = end;
            }

            if (!bus.NextStation.Valid || !StationList.Contains(bus.NextStation))
                continue;

            var nextArrival = (bus.NextTransfer > now ? bus.NextTransfer : now) + flightTime;
            if (bus.NextStation == grid && nextArrival < arrival)
                arrival = nextArrival;

            // Counter points past the cached NextStation; use the same order as TryGetNextStation.
            for (var i = 0; i < StationList.Count; i++)
            {
                if (StationList[(Counter + i) % StationList.Count] != grid)
                    continue;

                var laterArrival = nextArrival + interval * (i + 1);
                if (laterArrival < arrival)
                    arrival = laterArrival;
                break;
            }
        }

        return arrival != TimeSpan.MaxValue;
    }
}
