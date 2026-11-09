using Content.Shared.Popups;
using Content.Shared.Station.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Content.Server.Salvage.Expeditions; // Frontier
using Content.Shared._NF.CCVar; // Frontier
using Content.Shared.Mind.Components; // Frontier
using Content.Shared.Mobs.Components; // Frontier
using Content.Shared.NPC.Components; // Frontier
using Content.Shared.IdentityManagement; // Frontier
using Content.Shared.NPC; // Frontier
using Content.Server._NF.Salvage; // Frontier
using Content.Server.Shuttles.Components;
using Content.Shared.Access.Systems;
using Content.Server._NF.Shuttles.Components;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    public static readonly EntProtoId CoordinatesDisk = "CoordinatesDisk";
    private const float ShuttleFTLRange = 256f;
    private const float ShuttleFTLMassThreshold = 100f;

    [Dependency] private SharedPopupSystem _popupSystem = default!;
    [Dependency] private AccessReaderSystem _expeditionAccess = default!;
    [Dependency] private SharedHandsSystem _expeditionHands = default!;

    private EntityUid? GetExpeditionSource(EntityUid console, SalvageExpeditionConsoleComponent component,
        TransformComponent? xform = null)
    {
        xform ??= Transform(console);
        if (!component.DispatchByDisk && HasComp<SalvageExpeditionDataComponent>(xform.GridUid))
            return xform.GridUid;

        return _station.GetOwningStation(console, xform);
    }

    public EntityUid? GetExpeditionDepartureGrid(EntityUid source)
    {
        if (HasComp<MapGridComponent>(source))
            return source;

        return TryComp<StationDataComponent>(source, out var station)
            ? _station.GetLargestGrid((source, station))
            : null;
    }

    public bool GridExpeditionsEnabled(EntityUid grid)
    {
        if (TryComp<SalvageExpeditionDataComponent>(grid, out var data))
            return data.Enabled;

        var station = _station.GetOwningStation(grid);
        return TryComp<SalvageExpeditionDataComponent>(station, out data) &&
               data.Enabled && GetExpeditionDepartureGrid(station.Value) == grid;
    }

    public bool SetGridExpeditions(EntityUid grid, bool enabled)
    {
        if (GridExpeditionsEnabled(grid) == enabled)
            return true;

        var station = _station.GetOwningStation(grid);
        if ((TryComp<SalvageExpeditionDataComponent>(grid, out var data) && data.Claimed) ||
            (TryComp<SalvageExpeditionDataComponent>(station, out var stationData) && stationData.Claimed &&
             GetExpeditionDepartureGrid(station.Value) == grid))
            return false;

        data = EnsureComp<SalvageExpeditionDataComponent>(grid);
        data.Enabled = enabled;
        if (enabled && data.Missions.Count == 0)
            GenerateMissions(data);
        UpdateConsoles(grid, data);
        return true;
    }

    private void OnSalvageClaimMessage(EntityUid uid, SalvageExpeditionConsoleComponent component, ClaimSalvageMessage args)
    {
        var station = GetExpeditionSource(uid, component);

        // Frontier
        if (!TryComp<SalvageExpeditionDataComponent>(station, out var data) || !data.Enabled || data.Claimed ||
            HasComp<SalvageExpeditionComponent>(Transform(uid).MapUid) ||
            component.DispatchByDisk && !_expeditionAccess.IsAllowed(args.Actor, uid))
            return;

        var activeExpeditionCount = 0;
        var expeditionQuery = AllEntityQuery<SalvageExpeditionDataComponent, MetaDataComponent>();
        while (expeditionQuery.MoveNext(out var expeditionUid, out _, out _))
            if (TryComp<SalvageExpeditionDataComponent>(expeditionUid, out var expeditionData) && expeditionData.Claimed)
                activeExpeditionCount++;

        if (activeExpeditionCount >= _cfgManager.GetCVar(NFCCVars.SalvageExpeditionMaxActive))
        {
            PlayDenySound(uid, component);
            _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-too-many"), uid, PopupType.MediumCaution);
            UpdateConsoles(station.Value, data);
            return;
        }
        // End Frontier

        if (!data.Missions.TryGetValue(args.Index, out var missionparams))
            return;

        var departureGrid = GetExpeditionDepartureGrid(station.Value);
        if (!component.DispatchByDisk)
        {
            if (departureGrid is not { Valid: true } ship || !HasComp<ShuttleComponent>(ship))
                return;

            if (HasComp<ForceAnchorComponent>(ship) || IsExpeditionShuttleDocked(ship))
            {
                PlayDenySound(uid, component);
                var reason = HasComp<ForceAnchorComponent>(ship)
                    ? "shuttle-console-force-anchored"
                    : "salvage-expedition-shuttle-docked";
                _popupSystem.PopupEntity(Loc.GetString(reason), uid, PopupType.MediumCaution);
                return;
            }
        }

        // Frontier: FTL travel is currently restricted to expeditions and such, and so we need to put this here
        // until FTL changes for us in some way.
        if (!component.Debug && !component.DispatchByDisk)
        {
            if (departureGrid is not { Valid: true } grid)
                return;
            if (!TryComp<MapGridComponent>(grid, out var gridComp))
                return;

            // Frontier: check for FTL component - if one exists, the station won't be taken into FTL.
            if (HasComp<FTLComponent>(grid))
            {
                PlayDenySound(uid, component);
                _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-recharge"), uid, PopupType.MediumCaution);
                UpdateConsoles(station.Value, data); // Sure, why not?
                return;
            }

            var xform = Transform(grid);
            var bounds = xform.WorldMatrix.TransformBox(gridComp.LocalAABB).Enlarged(ShuttleFTLRange);
            var bodyQuery = GetEntityQuery<PhysicsComponent>();

            foreach (var other in _mapManager.FindGridsIntersecting(xform.MapID, bounds))
            {
                if (other.Owner == grid ||
                    !bodyQuery.TryGetComponent(other.Owner, out var body) ||
                    body.Mass < ShuttleFTLMassThreshold)
                {
                    continue;
                }

                PlayDenySound(uid, component);
                _popupSystem.PopupEntity(Loc.GetString("shuttle-ftl-proximity"), uid, PopupType.Medium);
                UpdateConsoles(station.Value, data);
                return;
            }
        }
        // End Frontier

        EntityUid? disk = null;
        if (component.DispatchByDisk)
        {
            disk = Spawn(CoordinatesDisk, Transform(args.Actor).Coordinates);
            _expeditionHands.TryPickupAnyHand(args.Actor, disk.Value);
            _popupSystem.PopupEntity(Loc.GetString("dl-underground-expedition-disk"), uid, args.Actor);
        }

        SpawnMission(missionparams, station.Value, uid, disk);

        data.ActiveMission = args.Index;
        var mission = GetMission(missionparams.MissionType, missionparams.Difficulty, missionparams.Seed);
        data.NextOffer = _timing.CurTime + mission.Duration + TimeSpan.FromSeconds(1);

        // Frontier  change - disable coordinate disks for expedition missions
        //_labelSystem.Label(cdUid, GetFTLName(_prototypeManager.Index<LocalizedDatasetPrototype>("NamesBorer"), missionparams.Seed));
        //_audio.PlayPvs(component.PrintSound, uid);

        UpdateConsoles(station.Value, data); // Frontier: add station
    }

    // Frontier: early expedition end
    private void OnSalvageFinishMessage(EntityUid entity, SalvageExpeditionConsoleComponent component, FinishSalvageMessage e)
    {
        var map = Transform(entity).MapUid;
        if (!TryComp<SalvageExpeditionComponent>(map, out var expedition))
            return;

        var station = expedition.DispatchedFromOutpost ? expedition.Station : GetExpeditionSource(entity, component);
        if (!TryComp<SalvageExpeditionDataComponent>(station, out var data) || !data.CanFinish)
            return;

        // Based on SalvageSystem.Runner:OnConsoleFTLAttempt
        if (!TryComp(entity, out TransformComponent? xform)) // Get the console's grid (if you move it, rip you)
        {
            PlayDenySound(entity, component);
            _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-shuttle-not-found"), entity, PopupType.MediumCaution);
            UpdateConsoles(station.Value, data);
            return;
        }

        // Frontier: check if any player characters or friendly ghost roles are outside
        var query = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var mindContainer, out var _, out var mobXform))
        {
            if (mobXform.MapUid != xform.MapUid)
                continue;

            // Not player controlled (ghosted)
            if (!mindContainer.HasMind)
                continue;

            // NPC, definitely not a person
            if (HasComp<ActiveNPCComponent>(uid) || HasComp<NFSalvageMobRestrictionsComponent>(uid))
                continue;

            // Hostile ghost role, continue
            if (TryComp(uid, out NpcFactionMemberComponent? npcFaction))
            {
                var hostileFactions = npcFaction.HostileFactions;
                if (hostileFactions.Contains("NanoTrasen")) // Nasty - what if we need pirate expeditions?
                    continue;
            }

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != xform.GridUid)
            {
                PlayDenySound(entity, component);
                _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-not-everyone-aboard", ("target", Identity.Entity(uid, EntityManager))), entity, PopupType.MediumCaution);
                UpdateConsoles(station.Value, data);
                return;
            }
        }
        // End SalvageSystem.Runner:OnConsoleFTLAttempt

        data.CanFinish = false;
        UpdateConsoles(station.Value, data);

        const int departTime = 20;
        var newEndTime = _timing.CurTime + TimeSpan.FromSeconds(departTime);

        if (expedition.EndTime <= newEndTime)
            return;

        expedition.EndTime = newEndTime;
        expedition.Stage = ExpeditionStage.FinalCountdown;
        Dirty(map.Value, expedition);

        Announce(map.Value, Loc.GetString("salvage-expedition-announcement-early-finish", ("departTime", departTime)));
    }
    // End Frontier: early expedition end

    private void OnSalvageConsoleInit(Entity<SalvageExpeditionConsoleComponent> console, ref ComponentInit args)
    {
        UpdateConsole(console);
    }

    private void OnSalvageConsoleParent(Entity<SalvageExpeditionConsoleComponent> console, ref EntParentChangedMessage args)
    {
        UpdateConsole(console);
    }

    private void UpdateConsoles(EntityUid stationUid, SalvageExpeditionDataComponent component)
    {
        var query = AllEntityQuery<SalvageExpeditionConsoleComponent, UserInterfaceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var console, out var uiComp, out var xform))
        {
            var aboardDispatchedExpedition = TryComp<SalvageExpeditionComponent>(xform.MapUid, out var expedition) &&
                                              expedition.DispatchedFromOutpost && expedition.Station == stationUid;
            if (!aboardDispatchedExpedition && GetExpeditionSource(uid, console, xform) != stationUid)
                continue;

            var state = GetState(component);
            if (console.DispatchByDisk)
                state.CanFinish = false;

            // Frontier: if we have a lingering FTL component, we cannot start a new mission
            if (!console.DispatchByDisk && !aboardDispatchedExpedition &&
                !CanOfferExpeditionDeparture(stationUid))
            {
                state.Cooldown = true; //Hack: disable buttons
            }
            // End Frontier

            _ui.SetUiState((uid, uiComp), SalvageConsoleUiKey.Expedition, state);
        }
    }

    private void UpdateConsole(Entity<SalvageExpeditionConsoleComponent> component)
    {
        var station = GetExpeditionSource(component, component.Comp);
        SalvageExpeditionConsoleState state;

        if (TryComp<SalvageExpeditionDataComponent>(station, out var dataComponent))
        {
            state = GetState(dataComponent);
        }
        else
        {
            state = new SalvageExpeditionConsoleState(TimeSpan.Zero, false, true, false, 0, new List<SalvageMissionParams>()); // Frontier: add false as 4th param
        }

        // Frontier: if we have a lingering FTL component, we cannot start a new mission
        if (!component.Comp.DispatchByDisk &&
            (station is not { } source || !CanOfferExpeditionDeparture(source)))
        {
            state.Cooldown = true; //Hack: disable buttons
        }
        // End Frontier

        _ui.SetUiState(component.Owner, SalvageConsoleUiKey.Expedition, state);
    }

    private bool CanOfferExpeditionDeparture(EntityUid source) =>
        GetExpeditionDepartureGrid(source) is { Valid: true } grid &&
        HasComp<ShuttleComponent>(grid) && !HasComp<FTLComponent>(grid);

    private void PlayDenySound(EntityUid uid, SalvageExpeditionConsoleComponent component)
    {
        _audio.PlayPvs(_audio.GetSound(component.ErrorSound), uid);
    }

    /// <summary>
    /// Expedition travel requires every docking connection to be released,
    /// including connections to small craft and grids with an FTL lock.
    /// </summary>
    public bool IsExpeditionShuttleDocked(EntityUid grid)
    {
        var query = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (query.MoveNext(out var dock, out var xform))
        {
            if (xform.GridUid == grid && dock.Docked)
                return true;
        }

        return false;
    }
}
