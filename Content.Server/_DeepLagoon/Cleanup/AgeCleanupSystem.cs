using System.Linq;
using Content.Server._DV.Mail.Components;
using Content.Server._Mono.Cleanup;
using Content.Server.Atmos.Components;
using Content.Server.NPC.HTN;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Misc;
using Robust.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._DeepLagoon.Cleanup;

public sealed partial class AgeCleanupSystem : BaseCleanupSystem<PhysicsComponent>
{
    [Dependency] private IGameTiming _clock = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly HashSet<EntityUid> _tracked = new();
    private static readonly TimeSpan DebrisLifetime = TimeSpan.FromMinutes(15);
    public int TrackedCount => _tracked.Count;

    public readonly record struct Candidate(NetEntity Entity, string Name, AgeCleanupKind Kind,
        TimeSpan Since, TimeSpan Due, int Units);

    public override void Initialize()
    {
        base.Initialize();
        _cleanupInterval = TimeSpan.FromSeconds(10);
        SubscribeLocalEvent<AgeCleanupComponent, ComponentStartup>((uid, _, _) => _tracked.Add(uid));
        SubscribeLocalEvent<AgeCleanupComponent, ComponentRemove>((uid, _, _) => _tracked.Remove(uid));
        SubscribeLocalEvent<AgeCleanupComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<NpcDroneCleanupComponent, ComponentStartup>(OnDroneStartup);
        SubscribeLocalEvent<NpcDroneCoreCleanupComponent, EntityTerminatingEvent>(OnCoreTerminating);
        SubscribeLocalEvent<NpcDroneCoreCleanupComponent, ComponentStartup>(OnCoreStartup);
        SubscribeLocalEvent<NpcDroneCoreCleanupComponent, EntParentChangedMessage>(OnCoreParentChanged);
        InitializeOrgans();
    }

    public override void Update(float frameTime)
    {
        var remaining = 128;
        UpdatePeriodicCleanup(frameTime, ref remaining, Stopwatch.GetTimestamp() + Math.Max(1, Stopwatch.Frequency / 1000));
    }

    public void MarkOpenedMail(EntityUid uid)
    {
        var seconds = _configuration.GetCVar(AgeCleanupCVars.OpenedMailSeconds);
        if (!float.IsFinite(seconds) || seconds <= 0)
            return;
        Mark(uid, AgeCleanupKind.OpenedMail, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400)));
    }

    private void Mark(EntityUid uid, AgeCleanupKind kind, TimeSpan delay)
    {
        var component = EnsureComp<AgeCleanupComponent>(uid);
        component.Kind = kind;
        component.Since = _clock.CurTime;
        component.Due = _clock.CurTime + delay;
        _tracked.Add(uid);
    }

    /// <summary>
    /// Root-level loose objects on a space map, including detached machinery. Classification does
    /// not include temporary recovery protection, so legacy cleanup cannot bypass that protection.
    /// </summary>
    public bool IsSpaceDebris(EntityUid uid)
    {
        if (!TryComp<TransformComponent>(uid, out var transform) || transform.MapUid is not { } map
            || transform.ParentUid != map || transform.GridUid != null || transform.Anchored
            || HasComp<MapComponent>(uid) || HasComp<MapGridComponent>(uid) || HasComp<MapGridComponent>(map)
            || HasComp<CleanupImmuneComponent>(uid) || HasComp<MindContainerComponent>(uid)
            || HasComp<ActorComponent>(uid) || HasComp<MobStateComponent>(uid) || HasComp<GhostComponent>(uid)
            || HasComp<ProjectileComponent>(uid))
            return false;
        return !TryComp<MapAtmosphereComponent>(map, out var atmosphere) || atmosphere.Space;
    }

    protected override bool ShouldEntityCleanup(EntityUid uid) => IsDue(uid);

    public bool IsDue(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
            return false;
        // Anatomy has its own lifetime even in space; legacy space cleanup must not shorten it.
        if (HasComp<Content.Shared.Body.Organ.OrganComponent>(uid) &&
            (!TryComp<AgeCleanupComponent>(uid, out var organAge) || organAge.Kind != AgeCleanupKind.LooseOrgan))
        {
            if (organAge != null) RemComp<AgeCleanupComponent>(uid);
            if (IsLooseOrgan(uid)) Mark(uid, AgeCleanupKind.LooseOrgan, TimeSpan.FromMinutes(10));
            return false;
        }
        var space = IsSpaceDebris(uid) && !HasPlayerTow(uid);
        if (!TryComp<AgeCleanupComponent>(uid, out var age))
        {
            if (space)
                Mark(uid, AgeCleanupKind.SpaceDebris, DebrisLifetime);
            else if (TryComp<NpcDroneCleanupComponent>(uid, out var drone) && drone.Defeated && IsUnclaimedDefeatedDrone(uid))
                Mark(uid, AgeCleanupKind.DefeatedDrone, DebrisLifetime);
            return false;
        }

        var eligible = age.Kind switch
        {
            AgeCleanupKind.SpaceDebris => space,
            AgeCleanupKind.OpenedMail => TryComp<MailComponent>(uid, out var mail) && !mail.IsEnabled
                && (!_containers.TryGetContainer(uid, "contents", out var contents) || contents.ContainedEntities.Count == 0),
            AgeCleanupKind.DefeatedDrone => IsUnclaimedDefeatedDrone(uid),
            AgeCleanupKind.LooseOrgan => IsLooseOrgan(uid),
            _ => false,
        };
        if (!eligible)
        {
            // Mail can temporarily contain an item; retain its deadline and wait until empty.
            if (age.Kind != AgeCleanupKind.OpenedMail)
                RemComp<AgeCleanupComponent>(uid);
            return false;
        }
        return !MetaData(uid).EntityPaused && _clock.CurTime >= age.Due && !ContainsPlayer(uid)
            && (age.Kind != AgeCleanupKind.LooseOrgan || !IsOrganObserved(uid));
    }

    private void OnParentChanged(EntityUid uid, AgeCleanupComponent age, ref EntParentChangedMessage args)
    {
        if (age.Kind == AgeCleanupKind.LooseOrgan && !IsLooseOrgan(uid))
            RemComp<AgeCleanupComponent>(uid);
        if (age.Kind == AgeCleanupKind.SpaceDebris && !IsSpaceDebris(uid))
            RemComp<AgeCleanupComponent>(uid);
    }

    public IReadOnlyList<Candidate> GetCandidates(int limit = 200)
    {
        var result = new List<Candidate>(Math.Clamp(limit, 0, 1000));
        foreach (var uid in _tracked)
        {
            if (result.Count >= limit)
                break;
            if (TerminatingOrDeleted(uid) || !TryComp<AgeCleanupComponent>(uid, out var age))
                continue;
            result.Add(new(GetNetEntity(uid), Name(uid), age.Kind, age.Since, age.Due,
                TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1));
        }
        return result;
    }

    /// <summary>Uses the very same eligibility rules for administrative and automatic cleanup.</summary>
    public int DeleteEligible(IEnumerable<NetEntity> requested, int limit = 32)
    {
        var deleted = 0;
        foreach (var net in requested.Take(Math.Clamp(limit, 0, 32)))
            if (TryGetEntity(net, out var uid) && uid is { } entity && IsDue(entity) && CleanupEnt(entity))
                deleted++;
        return deleted;
    }

    private bool ContainsPlayer(EntityUid root)
    {
        var pending = new Stack<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (HasComp<ActorComponent>(uid) || TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind)
                return true;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }
        return false;
    }

    private bool IsDroneCore(EntityUid uid) => Prototype(uid) is { } prototype &&
        _prototypes.EnumerateAllParents<EntityPrototype>(prototype.ID, includeSelf: true)
            .Any(parent => parent.id == "NpcStationAiRammer");

    private void OnDroneStartup(EntityUid uid, NpcDroneCleanupComponent drone, ComponentStartup args)
    {
        drone.Cores.Clear();
        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
            if (HasComp<HTNComponent>(child) && IsDroneCore(child))
                drone.Cores.Add(child);
        drone.CoresInitialized = true;
        if (drone.Cores.Count > 0)
            drone.Defeated = false;
    }

    private void OnCoreStartup(EntityUid uid, NpcDroneCoreCleanupComponent core, ComponentStartup args)
    {
        if (!IsDroneCore(uid) || Transform(uid).GridUid is not { } grid || !TryComp<NpcDroneCleanupComponent>(grid, out var drone))
            return;
        drone.Cores.Add(uid);
        drone.Defeated = false;
        if (TryComp<AgeCleanupComponent>(grid, out var age) && age.Kind == AgeCleanupKind.DefeatedDrone)
            RemComp<AgeCleanupComponent>(grid);
    }

    private void OnCoreTerminating(EntityUid uid, NpcDroneCoreCleanupComponent core, ref EntityTerminatingEvent args)
    {
        if (!IsDroneCore(uid) || Transform(uid).GridUid is not { } grid || TerminatingOrDeleted(grid)
            || !TryComp<NpcDroneCleanupComponent>(grid, out var drone))
            return;
        drone.Cores.Remove(uid);
        if (drone.CoresInitialized && !drone.Cores.Any(entity => !TerminatingOrDeleted(entity)))
        {
            drone.Defeated = true;
            Mark(grid, AgeCleanupKind.DefeatedDrone, DebrisLifetime);
        }
    }

    private void OnCoreParentChanged(EntityUid uid, NpcDroneCoreCleanupComponent core, ref EntParentChangedMessage args)
    {
        if (!IsDroneCore(uid))
            return;
        if (args.OldParent is { } old && !TerminatingOrDeleted(old) && TryComp<NpcDroneCleanupComponent>(old, out var previous))
        {
            previous.Cores.Remove(uid);
            if (previous.CoresInitialized && !previous.Cores.Any(entity => !TerminatingOrDeleted(entity)))
            {
                previous.Defeated = true;
                Mark(old, AgeCleanupKind.DefeatedDrone, DebrisLifetime);
            }
        }
        if (Transform(uid).GridUid is { } grid && TryComp<NpcDroneCleanupComponent>(grid, out var drone))
        {
            drone.Cores.Add(uid);
            drone.Defeated = false;
            if (TryComp<AgeCleanupComponent>(grid, out var age) && age.Kind == AgeCleanupKind.DefeatedDrone)
                RemComp<AgeCleanupComponent>(grid);
        }
    }

    private bool IsUnclaimedDefeatedDrone(EntityUid uid)
    {
        if (!TryComp<NpcDroneCleanupComponent>(uid, out var drone) || !HasComp<MapGridComponent>(uid)
            || HasComp<ShipOwnershipComponent>(uid) || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(uid)
            || drone.Cores.Any(entity => !TerminatingOrDeleted(entity)) || HasPlayerTow(uid))
            return false;
        // A docked/towed wreck has been recovered. Never delete the attached player's craft.
        foreach (var dock in EntityManager.System<Content.Server.Shuttles.Systems.DockingSystem>().GetDocks(uid))
            if (dock.Comp.DockedWith != null)
                return false;
        return true;
    }

    private bool IsPlayerTowAnchor(EntityUid uid, EntityUid wreck)
    {
        if (TerminatingOrDeleted(uid))
            return false;
        if (HasComp<ActorComponent>(uid) || TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind)
            return true;
        if (uid != wreck && (HasComp<ShipOwnershipComponent>(uid)
                || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(uid)))
            return true;
        var grid = Transform(uid).GridUid;
        return grid is { } ship && ship != wreck &&
            (HasComp<ShipOwnershipComponent>(ship) || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(ship));
    }

    private bool HasPlayerTow(EntityUid uid)
    {
        if (TryComp<TetheredComponent>(uid, out var tether) && IsPlayerTowAnchor(tether.Tetherer, uid))
            return true;
        if (TryComp<JointComponent>(uid, out var joints) && joints.GetJoints.Values.Any(joint =>
            IsPlayerTowAnchor(joint.BodyAUid, uid) || IsPlayerTowAnchor(joint.BodyBUid, uid)))
            return true;
        if (!TryComp<JointRelayTargetComponent>(uid, out var relay))
            return false;
        foreach (var hook in relay.Relayed)
            if (TryComp<JointComponent>(hook, out var hookJoints) && hookJoints.GetJoints.Values.Any(joint =>
                    IsPlayerTowAnchor(joint.BodyAUid, uid) || IsPlayerTowAnchor(joint.BodyBUid, uid)))
                return true;
        return false;
    }
}
