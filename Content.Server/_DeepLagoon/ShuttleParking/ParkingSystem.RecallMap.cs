using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Pinpointer;
using Content.Shared.Station.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private ParkingRecallMap? BuildRecallMap(EntityUid console, EntityUid ship)
    {
        var consoleTransform = Transform(console);
        if (consoleTransform.GridUid is not { } reference || _stations.GetOwningStation(console, consoleTransform) is not { } station
            || !TryComp<StationDataComponent>(station, out var stationData)
            || !TryComp<FixturesComponent>(ship, out var shipFixtures))
            return null;

        var stationHull = new List<Vector2[]>();
        var ports = new List<ParkingRecallPort>();
        var shipHull = SharedNavMapSystem.GetFloorPolygons(shipFixtures).ToList();
        var shipDocks = _docking.GetDocks(ship).Where(dock => Transform(dock.Owner).Anchored).ToList();
        var referenceInverse = _transform.GetInvWorldMatrix(reference);
        var referenceAngle = _transform.GetWorldRotation(reference);
        var shownGrids = new HashSet<EntityUid>();
        var obstructions = new HashSet<EntityUid>();
        var portObstructions = new HashSet<EntityUid>();

        foreach (var grid in stationData.Grids)
        {
            if (TerminatingOrDeleted(grid) || !HasComp<MapGridComponent>(grid) || Transform(grid).MapUid != consoleTransform.MapUid
                || !TryComp<FixturesComponent>(grid, out var stationFixtures))
                continue;

            var gridToMap = _transform.GetWorldMatrix(grid) * referenceInverse;
            var gridAngle = _transform.GetWorldRotation(grid) - referenceAngle;
            AddHull(stationFixtures, gridToMap);
            shownGrids.Add(grid);

            foreach (var dock in _docking.GetDocks(grid))
            {
                var dockTransform = Transform(dock.Owner);
                if (!dockTransform.Anchored || dockTransform.GridUid != grid)
                    continue;

                portObstructions.Clear();
                var config = SelectedDockConfig(ship, grid, dock.Owner, shipDocks, portObstructions);
                var status = dock.Comp.DockedWith != null ? ParkingPortStatus.Occupied
                    : config != null ? ParkingPortStatus.Fits : ParkingPortStatus.Blocked;
                if (status == ParkingPortStatus.Blocked)
                    obstructions.UnionWith(portObstructions);
                Vector2? shipOrigin = null;
                var shipAngle = Angle.Zero;
                if (config != null)
                {
                    shipOrigin = Vector2.Transform(config.Coordinates.Position, gridToMap);
                    shipAngle = (config.Angle + gridAngle).Reduced();
                }
                else if (shipDocks.Count > 0)
                {
                    // Draw the attempted pose even if the docking solver rejects it.
                    var candidate = shipDocks.FirstOrDefault(candidate => (candidate.Comp.DockType & dock.Comp.DockType) != 0);
                    if (!candidate.Owner.Valid)
                        candidate = shipDocks[0];
                    var pose = DockingSystem.GetDockingPose(Transform(candidate.Owner), dockTransform);
                    shipOrigin = Vector2.Transform(pose.Transform.Translation, gridToMap);
                    shipAngle = (pose.Angle + gridAngle).Reduced();
                }

                var netDock = GetNetEntity(dock.Owner);
                ports.Add(new(netDock, Name(dock.Owner) + " · " + netDock,
                    Vector2.Transform(dockTransform.LocalPosition, gridToMap), status, shipOrigin, shipAngle));
            }
        }

        foreach (var grid in obstructions)
        {
            if (shownGrids.Add(grid) && TryComp<FixturesComponent>(grid, out var fixtures))
                AddHull(fixtures, _transform.GetWorldMatrix(grid) * referenceInverse);
        }

        return new ParkingRecallMap(stationHull, shipHull, ports);

        void AddHull(FixturesComponent fixtures, Matrix3x2 gridToMap)
        {
            foreach (var polygon in SharedNavMapSystem.GetFloorPolygons(fixtures))
            {
                for (var i = 0; i < polygon.Length; i++)
                    polygon[i] = Vector2.Transform(polygon[i], gridToMap);
                stationHull.Add(polygon);
            }
        }
    }
}
