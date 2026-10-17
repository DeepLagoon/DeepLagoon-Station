using Content.Shared._Exodus.FireControl;
// Copyright Rane (elijahrane@gmail.com) 2025
// All rights reserved. Relicensed under AGPL with permission

using Content.Server._Mono.Ships.Systems;
using Content.Server.Administration.Logs;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.FireControl;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.Database;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.UserInterface;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using System.Linq;
using System.Numerics;

namespace Content.Server._Mono.FireControl;

public sealed partial class FireControlSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ShuttleConsoleSystem _shuttleConsoleSystem = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private CrewedShuttleSystem _crewedShuttle = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    private SharedMapSystem _mapMan => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();

    private void InitializeConsole()
    {
        // Exodus: Component lifecycle events replace the previous player-spawn-wide refresh.
        SubscribeLocalEvent<FireControlConsoleComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<FireControlConsoleComponent, ComponentShutdown>(OnComponentShutdown);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleRefreshServerMessage>(OnRefreshServer);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleFireMessage>(OnFire);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleCursorPositionMessage>(OnCursorPosition); // Exodus fire-control cursor optimization
        SubscribeLocalEvent<FireControlConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
        SubscribeLocalEvent<FireControlConsoleComponent, ActivatableUIOpenAttemptEvent>(OnConsoleUIOpenAttempt);
    }

    private void OnPowerChanged(EntityUid uid, FireControlConsoleComponent component, PowerChangedEvent args)
    {
        if (args.Powered)
            TryRegisterConsole(uid, component);
        else
            UnregisterConsole(uid, component);

        QueueConsoleUiUpdate(uid); // Exodus fire-control event-driven UI updates
    }

    private void OnComponentShutdown(EntityUid uid, FireControlConsoleComponent component, ComponentShutdown args)
    {
        UnregisterConsole(uid, component);
        _pendingUiConsoles.Remove(uid); // Exodus fire-control event-driven UI updates
        _nextUiUpdates.Remove(uid); // Exodus fire-control event-driven UI updates
    }

    private void DoRefreshServer(EntityUid uid, FireControlConsoleComponent component)
    {
        // First, clean up any invalid server references across all grids
        CleanupInvalidServerReferences();

        // Get the console's grid to force server reconnection on it
        var consoleGrid = _xform.GetGrid(uid);
        if (consoleGrid != null)
        {
            // Force all servers on this grid to attempt reconnection
            ForceServerReconnectionOnGrid((EntityUid)consoleGrid);
        }

        TryRegisterConsole(uid, component); // Exodus fire-control event-driven UI updates

        // Refresh controllables if we have a valid server connection
        if (component.ConnectedServer != null &&
            TryComp<FireControlServerComponent>(component.ConnectedServer, out var server) &&
            server.ConnectedGrid != null)
        {
            RefreshControllables((EntityUid)server.ConnectedGrid, immediateConsole: uid); // Exodus fire-control event-driven UI updates
        }

        // Always update UI to reflect current state
        UpdateUi(uid, component);
        MarkConsoleUiUpdated(uid); // Exodus fire-control event-driven UI updates
    }

    private void OnRefreshServer(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleRefreshServerMessage args)
    {
        DoRefreshServer(uid, component);
    }

    private void OnFire(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleFireMessage args)
    {
        if (component.ConnectedServer == null
            || !TryComp<FireControlServerComponent>(component.ConnectedServer, out var server)
            || !server.Consoles.Contains(uid))
            return;

        var xform = Transform(uid);
        var grid = xform.GridUid;
        if (grid == null || server.ConnectedGrid != grid)
            return;
        var coordinates = GetCoordinates(args.Coordinates);
        if (!coordinates.IsValid(EntityManager) || _transform.ToMapCoordinates(coordinates).MapId != xform.MapID)
            return;
        if (args.Selected.Count == 0)
        {
            RaiseCursorPosition(uid, coordinates);
            return;
        }

        // Fire the actual weapons
        FireWeapons((EntityUid)component.ConnectedServer, args.Selected, args.Coordinates, args.Actor, server, uid);
        if ((component.NextLog == null || component.NextLog < _timing.CurTime) && args.Selected.Any())
        {
            var firePos = _transform.ToMapCoordinates(GetCoordinates(args.Coordinates)).Position;
            var ourPos = _transform.GetWorldPosition(grid.Value);
            var grids = new List<Entity<MapGridComponent>>();
            var adjust = new Vector2(component.LogGridLookupRange, component.LogGridLookupRange);
            _mapMan.FindGridsIntersecting(xform.MapID, new Box2(firePos - adjust, firePos + adjust), ref grids, approx: true, includeMap: false);
            grids.RemoveAll(g => g == grid);
            EntityUid? closest = null;
            foreach (var gridUid in grids)
            {
                var newPos = _transform.GetWorldPosition(gridUid);
                if (closest == null || (newPos - firePos).LengthSquared() < (_transform.GetWorldPosition(closest.Value) - firePos).LengthSquared())
                    closest = gridUid;
            }

            _adminLogger.Add(LogType.ShipgunFired, LogImpact.High,
                    $"{ToPrettyString(args.Actor):user} fired weaponry of ship {ToPrettyString(grid):entity} from ({ourPos}) to ({firePos}), closest grid: {ToPrettyString(closest)}");

            component.NextLog = _timing.CurTime + component.LogSpacing;
        }

        // Exodus-begin fire-control cursor optimization
        RaiseCursorPosition(uid, coordinates);
        // Exodus-end
    }

    // Exodus-begin fire-control cursor optimization
    private void OnCursorPosition(Entity<FireControlConsoleComponent> ent, ref FireControlConsoleCursorPositionMessage args)
    {
        if (!_consoleMousePositions.ContainsKey(ent))
            return;

        var grid = Transform(ent).GridUid;
        if (grid == null
            || ent.Comp.ConnectedServer == null
            || !TryComp<FireControlServerComponent>(ent.Comp.ConnectedServer, out var server)
            || !server.Consoles.Contains(ent)
            || server.ConnectedGrid != grid)
        {
            return;
        }

        var coordinates = GetCoordinates(args.Coordinates);
        if (!coordinates.IsValid(EntityManager))
            return;

        RaiseCursorPosition(ent, coordinates);
    }

    private void RaiseCursorPosition(EntityUid console, EntityCoordinates coordinates)
    {
        var cursorEvent = new FireControlConsoleCursorPositionEvent(coordinates);
        RaiseLocalEvent(console, ref cursorEvent);
    }
    // Exodus-end

    public void OnUIOpened(EntityUid uid, FireControlConsoleComponent component, BoundUIOpenedEvent args)
    {
        DoRefreshServer(uid, component); // Exodus fire-control event-driven UI updates
    }

    private void OnConsoleUIOpenAttempt(
        EntityUid uid,
        FireControlConsoleComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        // A pilot console exposes one combined UI, rather than opening a second gunner UI.
        if (_ui.HasUi(uid, Content.Shared.Shuttles.Components.ShuttleConsoleUiKey.Key))
            return;

        var shuttle = _transform.GetParentUid(uid);
        var uiOpen = _crewedShuttle.AnyShuttleConsoleActiveByPlayer(shuttle, args.User);
        var forceOne = HasComp<CrewedShuttleComponent>(shuttle) && !HasComp<AdvancedPilotComponent>(args.User);

        // Crewed shuttles should not allow people to have both gunnery and shuttle consoles open.
        if (uiOpen && forceOne)
        {
            args.Cancel();
            _popup.PopupClient(Loc.GetString("shuttle-console-crewed"), args.User);
        }
    }

    private void UnregisterConsole(EntityUid console, FireControlConsoleComponent? component = null)
    {
        if (!Resolve(console, ref component))
            return;

        if (component.ConnectedServer == null)
            return;

        // Check if server still exists before trying to unregister
        if (Exists(component.ConnectedServer) && TryComp<FireControlServerComponent>(component.ConnectedServer, out var server))
        {
            server.Consoles.Remove(console);
        }

        component.ConnectedServer = null;
    }

    private bool CanRegister((EntityUid? ServerUid, FireControlServerComponent? ServerComponent) gridServer)
    {
        if (gridServer.ServerComponent == null)
            return false;

        if (gridServer.ServerComponent.EnforceMaxConsoles
            && gridServer.ServerComponent.Consoles.Count >= gridServer.ServerComponent.MaxConsoles)
            return false;

        return true;
    }

    private bool TryRegisterConsole(EntityUid console, FireControlConsoleComponent? consoleComponent = null)
    {
        if (!Resolve(console, ref consoleComponent))
            return false;

        // Exodus-begin fire-control event-driven UI updates
        var canOperate = Transform(console).Anchored && _power.IsPowered(console);
        var gridServer = TryGetGridServer(console);

        if (consoleComponent.ConnectedServer is { } connectedServer)
        {
            var connectionValid = canOperate
                && gridServer.ServerUid == connectedServer
                && gridServer.ServerComponent != null
                && gridServer.ServerComponent.Consoles.Contains(console);

            if (connectionValid)
                return true;

            if (TryComp<FireControlServerComponent>(connectedServer, out var previousServer))
                previousServer.Consoles.Remove(console);

            consoleComponent.ConnectedServer = null;
        }

        if (!canOperate || gridServer.ServerUid == null || gridServer.ServerComponent == null)
            return false;
        // Exodus-end

        var canRegister = CanRegister(gridServer);

        if (canRegister && gridServer.ServerComponent.Consoles.Add(console))
        {
            consoleComponent.ConnectedServer = gridServer.ServerUid;
            return true;
        }

        return false;
    }

    private void UpdateUi(EntityUid uid, FireControlConsoleComponent? component = null)
    {
        if (!IsConsoleUiOpen(uid))
            return;
        var state = GetConsoleState(uid);
        if (state == null)
            return;
        if (_ui.IsUiOpen(uid, FireControlConsoleUiKey.Key))
            _ui.SetUiState(uid, FireControlConsoleUiKey.Key, state);
        if (_ui.IsUiOpen(uid, Content.Shared.Shuttles.Components.ShuttleConsoleUiKey.Key))
            _shuttleConsoleSystem.RefreshState(uid);
    }

    public FireControlConsoleBoundInterfaceState? GetConsoleState(EntityUid uid, NavInterfaceState? navState = null)
    {
        if (!TryComp<FireControlConsoleComponent>(uid, out var component))
            return null;

        navState ??= _shuttleConsoleSystem.GetNavState(uid, _shuttleConsoleSystem.GetAllDocks());

        List<FireControllableEntry> controllables = new();
        var connected = false;
        if (component.ConnectedServer != null && TryComp<FireControlServerComponent>(component.ConnectedServer, out var server)
            && server.Consoles.Contains(uid))
        {
            connected = true;

            foreach (var controllable in server.Controlled)
            {
                var controlled = new FireControllableEntry();
                controlled.NetEntity = EntityManager.GetNetEntity(controllable);
                controlled.Coordinates = GetNetCoordinates(Transform(controllable).Coordinates);
                controlled.Name = MetaData(controllable).EntityName;
                TryComp<FireControllableComponent>(controllable, out var fcComp);
                controlled.IgnoresLos = fcComp != null ? fcComp.IgnoreLos : false;

                var (ammoCount, hasManualReload) = GetWeaponAmmunitionInfo(controllable);
                controlled.AmmoCount = ammoCount;
                controlled.HasManualReload = hasManualReload;
                if (TryComp<GunComponent>(controllable, out var gun))
                {
                    controlled.NextFire = gun.NextFire;
                    controlled.ReloadDuration = gun.FireRate > 0 ? 1f / gun.FireRate : 0;
                }

                controllables.Add(controlled);
            }
        }

        var array = controllables.ToArray();

        var state = new FireControlConsoleBoundInterfaceState(connected, array, navState)
        {
            ShieldState = Transform(uid).GridUid is { } grid
                ? EntityManager.System<Content.Server._Crescent.ShipShields.ShipShieldsSystem>().GetShieldState(grid) : null
        };
        return state;
    }

    /// <summary>
    /// Gets ammo information for a weapon to determine if it has manual reload.
    /// </summary>
    private (int? ammoCount, bool hasManualReload) GetWeaponAmmunitionInfo(EntityUid weaponEntity)
    {
        if (TryComp<BasicEntityAmmoProviderComponent>(weaponEntity, out var basicAmmo))
        {
            var hasRecharge = HasComp<RechargeBasicEntityAmmoComponent>(weaponEntity);

            return (basicAmmo.Count, !hasRecharge);
        }

        if (TryComp<BallisticAmmoProviderComponent>(weaponEntity, out var ballisticAmmo))
        {
            // if we're InfiniteUnspawned consider us to be non-reloading when at 0 ammo
            return (ballisticAmmo.Count, ballisticAmmo.Cycleable && (ballisticAmmo.Count != 0 || !ballisticAmmo.InfiniteUnspawned));
        }

        if (TryComp<MagazineAmmoProviderComponent>(weaponEntity, out var magazineAmmo))
        {
            var magazineEntity = GetMagazineEntity(weaponEntity);
            if (magazineEntity != null)
            {
                if (TryComp<BallisticAmmoProviderComponent>(magazineEntity, out var magazineBallisticAmmo))
                {
                    var hasAmmo = magazineBallisticAmmo.Cycleable
                             && (magazineBallisticAmmo.Count != 0 || !magazineBallisticAmmo.InfiniteUnspawned);
                    return (magazineBallisticAmmo.Count, hasAmmo);
                }

                if (TryComp<BasicEntityAmmoProviderComponent>(magazineEntity, out var magazineBasicAmmo))
                {
                    var hasRecharge = HasComp<RechargeBasicEntityAmmoComponent>(magazineEntity);
                    return (magazineBasicAmmo.Count, !hasRecharge);
                }
            }
        }

        if (TryComp<ProjectileBatteryAmmoProviderComponent>(weaponEntity, out _)
            || TryComp<HitscanBatteryAmmoProviderComponent>(weaponEntity, out _)
            )
        {
            var amm = new GetAmmoCountEvent();
            RaiseLocalEvent(weaponEntity, ref amm, false);
            return (amm.Count, true);
        }

        return (null, false);
    }

    /// <summary>
    /// Gets the magazine entity from a weapon's magazine slot.
    /// </summary>
    private EntityUid? GetMagazineEntity(EntityUid weaponEntity)
    {
        if (!_containers.TryGetContainer(weaponEntity, "gun_magazine", out var container) ||
            container is not ContainerSlot slot)
        {
            return null;
        }

        return slot.ContainedEntity;
    }
}
