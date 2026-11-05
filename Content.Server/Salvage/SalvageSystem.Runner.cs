using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Salvage.Expeditions;
using Content.Server.Salvage.Expeditions.Structure;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Map;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Localizations;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Content.Shared.Coordinates;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    /*
     * Handles actively running a salvage expedition.
     */

    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    private void InitializeRunner()
    {
        SubscribeLocalEvent<FTLRequestEvent>(OnFTLRequest);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnConsoleFTLAttempt);
    }

    private void OnConsoleFTLAttempt(ref ConsoleFTLAttemptEvent ev)
    {
        if (!TryComp(ev.Uid, out TransformComponent? xform) ||
            !TryComp<SalvageExpeditionComponent>(xform.MapUid, out var salvage))
        {
            return;
        }

        // TODO: This is terrible but need bluespace harnesses or something.
        var query = EntityQueryEnumerator<HumanoidAppearanceComponent, MobStateComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var _, out var mobState, out var mobXform))
        {
            if (mobXform.MapUid != xform.MapUid)
                continue;

            // Don't count unidentified humans (loot) or anyone you murdered so you can still maroon them once dead.
            if (_mobState.IsDead(uid, mobState))
                continue;

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != ev.Uid)
            {
                ev.Cancelled = true;
                ev.Reason = Loc.GetString("salvage-expedition-not-all-present");
                return;
            }
        }
    }

    /// <summary>
    /// Announces status updates to salvage crewmembers on the state of the expedition.
    /// </summary>
    private void Announce(EntityUid mapUid, string text)
    {
        var mapId = Comp<MapComponent>(mapUid).MapId;

        // I love TComms and chat!!!
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapId),
            ChatChannel.Radio,
            text,
            text,
            // During expedition shutdown the map has already left the map registry.
            mapUid,
            false,
            true,
            null);
    }

    private void OnFTLRequest(ref FTLRequestEvent ev)
    {
        if (!TryComp<SalvageExpeditionComponent>(ev.MapUid, out var expedition) ||
            !TryComp<FTLDestinationComponent>(ev.MapUid, out var dest))
        {
            return;
        }

        // Only one shuttle can occupy an expedition.
        dest.Enabled = false;
        Dirty(ev.MapUid, dest);
        if (expedition.DispatchExpiresAt != null)
            expedition.DispatchExpiresAt = _timing.CurTime + TimeSpan.FromMinutes(5);
        _shuttleConsoles.RefreshShuttleConsoles();
    }

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        if (!TryComp<SalvageExpeditionComponent>(args.MapUid, out var component))
            return;

        if (component.DispatchExpiresAt != null)
        {
            component.DispatchExpiresAt = null;
            component.EndTime = _timing.CurTime + component.MissionDuration;
            if (TryComp<FTLDestinationComponent>(args.MapUid, out var destination))
            {
                destination.Enabled = false;
                Dirty(args.MapUid, destination);
            }
        }

        // Frontier
        if (TryComp<SalvageExpeditionDataComponent>(component.Station, out var data))
        {
            data.CanFinish = true;
            UpdateConsoles(component.Station, data);
        }
        // Frontier

        // Someone FTLd there so start announcement
        if (component.Stage != ExpeditionStage.Added)
            return;

        Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", (component.EndTime - _timing.CurTime).Minutes)));

        var directionLocalization = ContentLocalizationManager.FormatDirection(component.DungeonLocation.GetDir()).ToLower();

        if (component.DungeonLocation != Vector2.Zero)
            Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-dungeon", ("direction", directionLocalization)));

        component.Stage = ExpeditionStage.Running;
        Dirty(args.MapUid, component);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        var destination = _transform.GetMap(ev.TargetCoordinates);
        if (ev.FromMapUid is { } origin && origin != destination &&
            TryComp<SalvageExpeditionComponent>(destination, out var incomingExpedition))
        {
            incomingExpedition.ReturnLocations[ev.Entity] =
                (new EntityCoordinates(origin, ev.FTLFrom.Translation), ev.FromRotation);
        }

        // Started a mining mission so work out exempt entities
        if (TryComp<SalvageMiningExpeditionComponent>(destination, out var mining))
        {
            var ents = new List<EntityUid>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            MiningTax(ents, ev.Entity, mining, xformQuery);
            mining.ExemptEntities = ents;
        }

        if (!TryComp<SalvageExpeditionComponent>(ev.FromMapUid, out var expedition) ||
            !TryComp<SalvageExpeditionDataComponent>(expedition.Station, out var station))
        {
            return;
        }

        station.CanFinish = false; // Frontier

        // Only the expedition's station can keep its mission active. Debris and
        // unrelated grids also have ShuttleComponent and may never leave this map.
        var query = EntityQueryEnumerator<ShuttleComponent, TransformComponent>();

        while (query.MoveNext(out var shuttleUid, out _, out var xform))
        {
            if (xform.MapUid == ev.FromMapUid &&
                (expedition.ReturnLocations.ContainsKey(shuttleUid) ||
                 _station.GetOwningStation(shuttleUid, xform) == expedition.Station))
                return;
        }

        // Last shuttle has left so finish the mission.
        QueueDel(ev.FromMapUid.Value);
    }

    // Runs the expedition
    private void UpdateRunner()
    {
        // Generic missions
        var query = AllEntityQuery<SalvageExpeditionComponent, MetaDataComponent>();

        // Run the basic mission timers (e.g. announcements, auto-FTL, completion, etc)
        while (query.MoveNext(out var uid, out var comp, out var metadata))
        {
            if (comp.DispatchExpiresAt is { } dispatchExpiry)
            {
                if (_timing.CurTime >= dispatchExpiry)
                    QueueDel(uid);
                continue;
            }

            if (metadata.EntityPaused)
                continue;

            var remaining = comp.EndTime - _timing.CurTime;
            var pendingDeparture = false;
            var audioLength = _audio.GetAudioLength(comp.SelectedSong);

            if (comp.Stage < ExpeditionStage.FinalCountdown && remaining < TimeSpan.FromSeconds(45))
            {
                comp.Stage = ExpeditionStage.FinalCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-seconds", ("duration", TimeSpan.FromSeconds(45).Seconds)));
            }
            else if (comp.Stage < ExpeditionStage.MusicCountdown && comp.Stream == null && remaining < audioLength) // Frontier
            {
                var audio = _audio.PlayPvs(comp.Sound, uid);
                comp.Stream = audio?.Entity;
                _audio.SetMapAudio(audio);
                comp.Stage = ExpeditionStage.MusicCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", audioLength.Minutes)));
            }
            else if (comp.Stage < ExpeditionStage.Countdown && remaining < TimeSpan.FromMinutes(5))
            {
                comp.Stage = ExpeditionStage.Countdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", TimeSpan.FromMinutes(5).Minutes)));
            }
            // Auto-FTL out any shuttles
            else if (remaining < TimeSpan.FromSeconds(_shuttle.DefaultStartupTime) + TimeSpan.FromSeconds(0.5))
            {
                var ftlTime = Math.Clamp((float) remaining.TotalSeconds - 0.5f, 0f, _shuttle.DefaultStartupTime);
                var shuttleQuery = AllEntityQuery<ShuttleComponent, TransformComponent>();
                var departingGroup = new HashSet<EntityUid>();

                while (shuttleQuery.MoveNext(out var shuttleUid, out var shuttle, out var shuttleXform))
                {
                    if (shuttleXform.MapUid != uid || departingGroup.Contains(shuttleUid))
                        continue;

                    if (TryComp<FTLComponent>(shuttleUid, out var ftl))
                    {
                        if (ftl.State == FTLState.Starting)
                        {
                            pendingDeparture = true;
                            _shuttle.GetAllDockedShuttles(shuttleUid, departingGroup);
                        }
                        continue;
                    }

                    var hasOrigin = comp.ReturnLocations.TryGetValue(shuttleUid, out var origin);
                    if (!hasOrigin && _station.GetOwningStation(shuttleUid, shuttleXform) != comp.Station)
                        continue;

                    if (!hasOrigin || TerminatingOrDeleted(origin.Coordinates.EntityId))
                    {
                        if (!_mapSystem.TryGetMap(_gameTicker.DefaultMap, out var fallbackMap))
                            continue;

                        origin = (new EntityCoordinates(fallbackMap.Value, new Vector2(1500f, 0f)), Angle.Zero);
                    }

                    _shuttle.FTLToCoordinates(shuttleUid, shuttle, origin.Coordinates, origin.Rotation, ftlTime, 50f, safeExpeditionReturn: true);
                    if (HasComp<FTLComponent>(shuttleUid))
                    {
                        pendingDeparture = true;
                        _shuttle.GetAllDockedShuttles(shuttleUid, departingGroup);
                    }
                }
            }

            if (remaining < TimeSpan.Zero && !pendingDeparture)
            {
                QueueDel(uid);
            }
        }

        // Mining missions: NOOP since it's handled after ftling

        // Structure missions
        var structureQuery = EntityQueryEnumerator<SalvageStructureExpeditionComponent, SalvageExpeditionComponent>();

        while (structureQuery.MoveNext(out var uid, out var structure, out var comp))
        {
            if (comp.Completed)
                continue;

            var structureAnnounce = false;

            for (var i = structure.Structures.Count - 1; i >= 0; i--)
            {
                var objective = structure.Structures[i];

                if (Deleted(objective))
                {
                    structure.Structures.RemoveSwap(i);
                    structureAnnounce = true;
                }
            }

            if (structureAnnounce)
            {
                comp.ObjectivesCompleted = comp.ObjectivesTotal - structure.Structures.Count;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-structure-remaining", ("count", structure.Structures.Count)));
            }

            if (structure.Structures.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }

        // Elimination missions
        var eliminationQuery = EntityQueryEnumerator<SalvageEliminationExpeditionComponent, SalvageExpeditionComponent>();
        while (eliminationQuery.MoveNext(out var uid, out var elimination, out var comp))
        {
            if (comp.Completed)
                continue;

            var announce = false;

            for (var i = elimination.Megafauna.Count - 1; i >= 0; i--)
            {
                var mob = elimination.Megafauna[i];

                if (Deleted(mob) || _mobState.IsDead(mob))
                {
                    elimination.Megafauna.RemoveSwap(i);
                    announce = true;
                }
            }

            if (announce)
            {
                comp.ObjectivesCompleted = comp.ObjectivesTotal - elimination.Megafauna.Count;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-megafauna-remaining", ("count", elimination.Megafauna.Count)));
            }

            if (elimination.Megafauna.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }
    }
}
