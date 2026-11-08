using System.Linq;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Maps;
using Content.Shared.Shuttles.Components;
using Content.Server.Shuttles.Components;
using Content.Server.StationEvents.Components;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    public int AppraiseParkedShuttle(EntityUid grid)
    {
        return (int) Math.Clamp(_pricing.AppraiseGrid(grid) * _baseSaleRate, 0, int.MaxValue);
    }

    /// <summary>Replace every reference to transient card/grid IDs when restoring a persistent vessel.</summary>
    public void BindParkingDeed(EntityUid grid, EntityUid card, string name, string owner)
    {
        foreach (var target in new[] { grid, card })
        {
            var deed = EnsureComp<ShuttleDeedComponent>(target);
            AssignShuttleDeedProperties(deed, grid, name, owner, false, null);
            deed.DeedHolder = card;
            Dirty(target, deed);
        }
        var pending = new Stack<EntityUid>(); pending.Push(grid);
        while (pending.TryPop(out var entity))
        {
            var children = Transform(entity).ChildEnumerator;
            while (children.MoveNext(out var child)) pending.Push(child);
            if (!HasComp<ShuttleConsoleComponent>(entity)) continue;
            var lockComp = EnsureComp<ShuttleConsoleLockComponent>(entity);
            _shuttleConsoleLock.SetShuttleId(entity, grid.ToString(), lockComp);
        }
        AddShipAccessToEntities(grid);
        EnsureComp<LinkedLifecycleGridParentComponent>(grid);
        _metaData.SetEntityName(grid, name);
    }
    /// <summary>Call only after arrival on the public sector map; station initialization registers an FTL destination.</summary>
    public void RegisterParkingStation(EntityUid grid)
    {
        if (TryComp<VesselComponent>(grid, out var vessel) && _prototypeManager.TryIndex<GameMapPrototype>(vessel.VesselId.Id, out var map)
            && map.Stations.TryGetValue(vessel.VesselId.Id, out var station))
            _station.InitializeNewStation(station, [grid], Name(grid));
    }
}
