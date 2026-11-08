using System.Linq;
using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.PowerCell;
using Content.Server.Salvage.Expeditions;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Events;
using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Pinpointer;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.Medical.CrewMonitoring;

public sealed partial class CrewMonitoringConsoleSystem : EntitySystem
{
    [Dependency] private PowerCellSystem _cell = default!;
    [Dependency] private UserInterfaceSystem _uiSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, DeviceNetworkPacketEvent>(OnPacketReceived);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
    }

    private void OnRemove(EntityUid uid, CrewMonitoringConsoleComponent component, ComponentRemove args)
    {
        component.ConnectedSensors.Clear();
    }

    private void OnPacketReceived(EntityUid uid, CrewMonitoringConsoleComponent component, DeviceNetworkPacketEvent args)
    {
        var payload = args.Data;

        // Check command
        if (!payload.TryGetValue(DeviceNetworkConstants.Command, out string? command))
            return;

        if (command != DeviceNetworkConstants.CmdUpdatedState)
            return;

        if (!payload.TryGetValue(SuitSensorConstants.NET_STATUS_COLLECTION, out Dictionary<string, SuitSensorStatus>? sensorStatus))
            return;

        component.ConnectedSensors = sensorStatus;
        UpdateUserInterface(uid, component);
    }

    private void OnUIOpened(EntityUid uid, CrewMonitoringConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (!_cell.TryUseActivatableCharge(uid))
            return;

        UpdateUserInterface(uid, component);
    }

    private void UpdateUserInterface(EntityUid uid, CrewMonitoringConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_uiSystem.IsUiOpen(uid, CrewMonitoringUIKey.Key))
            return;

        // The grid must have a NavMapComponent to visualize the map in the UI
        var xform = Transform(uid);

        if (xform.GridUid != null)
            EnsureComp<NavMapComponent>(xform.GridUid.Value);

        // Update all sensors info
        var allSensors = component.ConnectedSensors.Values
            .Select(status => GetSensorForMap(status, xform.MapID))
            .ToList();
        _uiSystem.SetUiState(uid, CrewMonitoringUIKey.Key, new CrewMonitoringState(allSensors));
    }

    private SuitSensorStatus GetSensorForMap(SuitSensorStatus status, MapId monitorMap)
    {
        if (status.Coordinates is not { } coordinates)
            return status;

        TryGetEntity(status.SuitSensorUid, out var sensor);
        TryComp<TransformComponent>(sensor, out var sensorTransform);

        // The sensor and the reference of its last GPS fix can be on different maps after a jump.
        if (monitorMap != MapId.Nullspace &&
            sensorTransform?.MapID == monitorMap &&
            TryGetEntity(coordinates.NetEntity, out var reference) &&
            TryComp<TransformComponent>(reference, out var referenceTransform) &&
            referenceTransform.MapID == monitorMap)
        {
            return status;
        }

        var location = status.LocationName;
        if (sensorTransform != null && sensorTransform.MapID != monitorMap &&
            HasComp<SalvageExpeditionComponent>(sensorTransform.MapUid))
        {
            location = sensorTransform.GridUid != sensorTransform.MapUid &&
                       TryComp<MetaDataComponent>(sensorTransform.GridUid, out var grid)
                ? Loc.GetString("crew-monitoring-location-expedition-shuttle", ("shuttle", grid.EntityName))
                : Loc.GetString("suit-sensor-location-expedition");
        }

        // Other consoles may still need the original GPS fix.
        return new SuitSensorStatus(status.SuitSensorUid, status.Name, status.Job, status.JobIcon, status.JobDepartments, location)
        {
            Timestamp = status.Timestamp,
            IsAlive = status.IsAlive,
            TotalDamage = status.TotalDamage,
            TotalDamageThreshold = status.TotalDamageThreshold,
        };
    }
}
