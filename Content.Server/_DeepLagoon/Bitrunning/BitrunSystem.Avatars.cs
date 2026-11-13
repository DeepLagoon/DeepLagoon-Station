using System.Linq;
using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Actions;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DragDrop;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Stunnable;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunAvatarComponent : Component
{
    public EntityUid Body;
    public EntityUid Mind;
    public EntityUid Pod;
    public EntityUid Server;
    public NetUserId User;
    public BitrunSide Side;
    public bool TookDamage;
}

[RegisterComponent]
public sealed partial class BitrunBodyComponent : Component
{
    public EntityUid Avatar;
}

public sealed partial class BitrunSystem
{
    private const string PodContainer = "body";

    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private GhostSystem _ghosts = default!;
    [Dependency] private StationSpawningSystem _spawning = default!;
    [Dependency] private SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, EntityUid> _pods = new();

    private void InitializeAvatars()
    {
        SubscribeLocalEvent<BitrunPodComponent, InteractUsingEvent>(OnPodProgram);
        SubscribeLocalEvent<BitrunPodComponent, DragDropTargetEvent>(OnPodDrop);
        SubscribeLocalEvent<BitrunPodComponent, ContainerRelayMovementEntityEvent>(OnPodMovement);
        SubscribeLocalEvent<BitrunPodComponent, EntRemovedFromContainerMessage>(OnBodyRemoved);
        SubscribeLocalEvent<BitrunPodComponent, EntityTerminatingEvent>(OnPodTerminating, before: [typeof(MindSystem)]);
        SubscribeLocalEvent<BitrunAvatarComponent, EntityTerminatingEvent>(OnAvatarTerminating,
            before: [typeof(MindSystem)]);
        SubscribeLocalEvent<BitrunAvatarComponent, DamageChangedEvent>(OnAvatarDamaged);
        SubscribeLocalEvent<BitrunAvatarComponent, BitrunDisconnectEvent>(OnDisconnectAction);
        SubscribeLocalEvent<BitrunBodyComponent, EntityTerminatingEvent>(OnBodyTerminating, before: [typeof(MindSystem)]);
        SubscribeLocalEvent<BitrunExitComponent, InteractHandEvent>(OnExitInteract);
    }

    private bool TryGetPodUser(EntityUid body, out Entity<MindComponent> mind, out NetUserId user)
    {
        mind = default;
        user = default;
        if (TerminatingOrDeleted(body) || !_mobState.IsAlive(body) || HasComp<BitrunAvatarComponent>(body) ||
            !HasComp<HumanoidAppearanceComponent>(body) || !TryComp<ActorComponent>(body, out var actor) ||
            !_mind.TryGetMind(body, out var mindId, out var component) || component.VisitingEntity != null ||
            component.UserId != actor.PlayerSession.UserId)
            return false;

        mind = (mindId, component);
        user = actor.PlayerSession.UserId;
        return true;
    }

    private void OnPodDrop(Entity<BitrunPodComponent> pod, ref DragDropTargetEvent args)
    {
        var body = args.Dragged;
        if (args.Handled || GetPodBody(pod) != null || !Transform(pod).Anchored ||
            !CanOperate(pod, args.User) || !TryGetPodUser(body, out _, out _) ||
            _containers.IsEntityInContainer(body) || TryComp<BuckleComponent>(body, out var buckle) && buckle.Buckled ||
            !_interaction.InRangeUnobstructed(body, pod.Owner) || !SeatBody(pod, body))
            return;

        args.Handled = true;
        _appearance.SetData(pod, BitrunMachineVisuals.Occupied, true);
        if (!_ui.TryOpenUi(pod.Owner, BitrunUiKey.Key, body))
            ReleaseBody(pod, body);
    }

    private EntityUid? GetPodBody(EntityUid pod) =>
        _containers.TryGetContainer(pod, PodContainer, out var slot) && slot is ContainerSlot body
            ? body.ContainedEntity
            : null;

    private void EnterPod(EntityUid pod, BitrunPodComponent component, EntityUid body, EntityUid server, BitrunSide side)
    {
        if (_pods.ContainsKey(pod) || _queuedPods.ContainsKey(pod) || !TryGetPodUser(body, out _, out var user) ||
            !CanJoinSession(pod, server) || GetPodBody(pod) is { } occupant && occupant != body)
            return;

        if (_lobbies.TryGetValue(server, out var lobby))
            Enroll(lobby, pod, body, user, side);
        else if (_runs.TryGetValue(server, out var run))
            ConnectAvatar(pod, component, body, run, side, user, alreadySeated: IsSeated(pod, body));
    }

    private bool SeatBody(EntityUid pod, EntityUid body)
    {
        var slot = _containers.EnsureContainer<ContainerSlot>(pod, PodContainer);
        return _containers.Insert(body, slot);
    }

    private bool IsSeated(EntityUid pod, EntityUid body) =>
        !TerminatingOrDeleted(pod) && !TerminatingOrDeleted(body) &&
        _containers.TryGetContainer(pod, PodContainer, out var slot) && slot.Contains(body);

    private void ReleaseBody(EntityUid pod, EntityUid body)
    {
        if (!Exists(pod))
            return;

        if (!TerminatingOrDeleted(pod))
        {
            _appearance.SetData(pod, BitrunMachineVisuals.Occupied, false);
            _ui.CloseUi(pod, BitrunUiKey.Key, body);
        }

        if (!TerminatingOrDeleted(body) && _containers.TryGetContainer(pod, PodContainer, out var slot) && slot.Contains(body))
            _containers.Remove(body, slot, force: true, destination: Transform(pod).Coordinates);
    }

    private bool ConnectAvatar(EntityUid pod, BitrunPodComponent component, EntityUid body, Run run,
        BitrunSide side, NetUserId user, bool alreadySeated)
    {
        if (!CanEnroll(run, side, user) || !TryGetPodUser(body, out var mind, out var currentUser) || currentUser != user)
            return false;

        if (alreadySeated ? !IsSeated(pod, body) : !SeatBody(pod, body))
            return false;

        var spawns = GetTeamSpawns(run, side);
        var point = spawns[run.Roster.Values.Count(player => player.Side == side)];

        EntityUid? avatar = null;
        try
        {
            var player = Spawn("MobHuman", point);
            avatar = player;
            var connection = AddComp<BitrunAvatarComponent>(player);
            connection.Body = body;
            connection.Mind = mind;
            connection.Pod = pod;
            connection.Server = run.Server;
            connection.Side = side;
            connection.User = user;
            EnsureComp<BitrunBodyComponent>(body).Avatar = player;

            _humanoid.CloneAppearance(body, player);
            var sideName = Loc.GetString(side == BitrunSide.Attackers ? "bitrun-side-attackers" : "bitrun-side-defenders");
            _metadata.SetEntityName(player, Loc.GetString("bitrun-avatar-side-name",
                ("side", sideName), ("name", MetaData(body).EntityName)));
            ProtoId<StartingGearPrototype> gear = run.Domain.Gear ?? component.Loadout switch
            {
                "medic" when !run.Domain.RestrictLoadout => "BitrunMedicGear",
                "security" when !run.Domain.RestrictLoadout => "BitrunSecurityGear",
                _ => "BitrunExplorerGear",
            };
            _spawning.EquipStartingGear(player, gear, raiseEvent: false);
            _actions.AddAction(player, "ActionBitrunDisconnect");
            ConfigureAvatarVision(run, player);
            ApplyProgram(run, component, player);

            run.Avatars.Add(player);
            run.Roster.Add(user, new Participant(side, player));
            _pods.Add(pod, player);
            _mind.TransferTo(mind, player, ghostCheckOverride: true, mind: mind.Comp);
            _appearance.SetData(pod, BitrunMachineVisuals.Occupied, true);
            var objective = side == BitrunSide.Attackers ? Loc.GetString(run.Domain.Description) : Loc.GetString("bitrun-defender-objective");
            _popup.PopupEntity(objective, player, player);
            return true;
        }
        catch (Exception error)
        {
            Log.Error($"Could not connect bitrun avatar: {error}");
            if (avatar is { } failedAvatar && _pods.ContainsKey(pod))
                Disconnect(failedAvatar, false);
            else
            {
                RemComp<BitrunBodyComponent>(body);
                ReleaseBody(pod, body);
                if (avatar is { } created)
                {
                    run.Avatars.Remove(created);
                    QueueDel(created);
                }
            }

            run.Roster.Remove(user);
            return false;
        }
    }

    private void OnPodProgram(EntityUid uid, BitrunPodComponent pod, InteractUsingEvent args)
    {
        if (args.Handled || !CanOperate(uid, args.User) || GetPodBody(uid) != null ||
            !HasComp<BitrunProgramDiskComponent>(args.Used) ||
            !_hands.IsHolding(args.User, args.Used) || MetaData(args.Used).EntityPrototype is not { } prototype)
            return;

        args.Handled = true;
        pod.Program = prototype.ID;
        _popup.PopupEntity(Loc.GetString("bitrun-program-loaded"), uid, args.User);
    }

    private void ApplyProgram(Run run, BitrunPodComponent pod, EntityUid avatar)
    {
        if (run.Domain.RestrictLoadout || pod.Program is not { } program)
            return;

        var disk = _prototypes.Index(program);
        if (!disk.TryComp<BitrunProgramDiskComponent>(out var data, EntityManager.ComponentFactory))
            return;

        if (data.Item is { } item)
            _hands.TryPickupAnyHand(avatar, Spawn(item, Transform(avatar).Coordinates), checkActionBlocker: false);

        if (data.Action is { } action)
            _actions.AddAction(avatar, action);
    }

    private void CheckAvatar(Run run, EntityUid avatar)
    {
        if (!TryComp<BitrunAvatarComponent>(avatar, out var link))
        {
            run.Avatars.Remove(avatar);
            foreach (var participant in run.Roster.Values)
            {
                if (participant.Avatar == avatar)
                    participant.Avatar = null;
            }
            return;
        }

        if (link.Side == BitrunSide.Attackers && _mobState.IsDead(avatar) && run.Domain.Objective == BitrunObjective.Island)
            AdvanceObjective(run, 1);

        if (!CanJoinSession(link.Pod, run.Server) || !HasComp<BitrunBodyComponent>(link.Body) ||
            !IsSeated(link.Pod, link.Body) || Transform(avatar).MapUid != run.Map ||
            !_mobState.IsAlive(link.Body) || !_mobState.IsAlive(avatar))
        {
            Disconnect(avatar, true, defeated: !_mobState.IsAlive(avatar));
            return;
        }

        if (!TryComp<ActorComponent>(avatar, out _) || !TryComp<MindComponent>(link.Mind, out var mind) ||
            mind.OwnedEntity != avatar)
            Disconnect(avatar, false);
    }

    private void Disconnect(EntityUid avatar, bool shock, bool defeated = false)
    {
        if (!TryComp<BitrunAvatarComponent>(avatar, out var link) ||
            !_pods.TryGetValue(link.Pod, out var occupant) || occupant != avatar)
            return;

        _pods.Remove(link.Pod);

        if (_runs.TryGetValue(link.Server, out var run))
        {
            run.Avatars.Remove(avatar);
            if (run.Roster.TryGetValue(link.User, out var participant))
            {
                participant.Avatar = null;
                participant.Defeated = defeated;
            }
        }

        if (!TerminatingOrDeleted(link.Body))
            RemComp<BitrunBodyComponent>(link.Body);

        ReleaseBody(link.Pod, link.Body);

        if (TryComp<MindComponent>(link.Mind, out var mind) &&
            (mind.OwnedEntity == avatar || mind.OwnedEntity == null ||
             HasComp<GhostComponent>(mind.OwnedEntity)))
        {
            if (!TerminatingOrDeleted(link.Body))
            {
                _mind.TransferTo(link.Mind, link.Body, ghostCheckOverride: true, mind: mind);
                if (shock)
                {
                    _stun.TryParalyze(link.Body, TimeSpan.FromSeconds(2), true);
                    _damage.TryChangeDamage(link.Body,
                        new DamageSpecifier(_prototypes.Index<DamageTypePrototype>("Blunt"), 5),
                        ignoreResistances: true);
                }
            }
            else
            {
                var origin = TryComp<TransformComponent>(link.Pod, out var podTransform)
                    ? podTransform.Coordinates
                    : (EntityCoordinates?) null;
                _ghosts.SpawnGhost((link.Mind, mind), origin, canReturn: false);
            }
        }

        if (!TerminatingOrDeleted(avatar))
            QueueDel(avatar);
    }

    private void OnBodyRemoved(EntityUid uid, BitrunPodComponent component, EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != PodContainer)
            return;

        if (_pods.TryGetValue(uid, out var avatar))
            Disconnect(avatar, true);
        else if (_queuedPods.ContainsKey(uid))
            LeaveQueue(uid);
        else
            ReleaseBody(uid, args.Entity);
    }

    private void OnPodMovement(Entity<BitrunPodComponent> pod, ref ContainerRelayMovementEntityEvent args)
    {
        if (!_pods.ContainsKey(pod) && GetPodBody(pod) == args.Entity)
            LeavePod(pod, args.Entity);
    }

    private void LeavePod(EntityUid pod, EntityUid body)
    {
        if (GetQueuedBody(pod) == body)
            LeaveQueue(pod);
        else
            ReleaseBody(pod, body);
    }

    private void OnPodTerminating(Entity<BitrunPodComponent> ent, ref EntityTerminatingEvent args)
    {
        if (_pods.TryGetValue(ent, out var avatar))
            Disconnect(avatar, true);
        else if (GetPodBody(ent) is { } body)
            LeavePod(ent, body);
    }

    private void OnAvatarTerminating(Entity<BitrunAvatarComponent> ent, ref EntityTerminatingEvent args) =>
        Disconnect(ent, true, defeated: true);

    private void OnBodyTerminating(Entity<BitrunBodyComponent> ent, ref EntityTerminatingEvent args) =>
        Disconnect(ent.Comp.Avatar, true);

    private void OnAvatarDamaged(EntityUid uid, BitrunAvatarComponent component, DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta == null || TerminatingOrDeleted(component.Body))
            return;

        var linked = new DamageSpecifier();
        foreach (var (type, amount) in args.DamageDelta.DamageDict)
        {
            if (amount > 0 && type is not ("Asphyxiation" or "Bloodloss"))
                linked.DamageDict[type] = amount;
        }

        if (linked.GetTotal() <= 0)
            return;

        component.TookDamage = true;
        _damage.TryChangeDamage(component.Body, linked * 0.5f, ignoreResistances: true);
    }

    private void OnDisconnectAction(EntityUid uid, BitrunAvatarComponent component, BitrunDisconnectEvent args)
    {
        args.Handled = true;
        Disconnect(uid, false);
    }

    private void OnExitInteract(EntityUid uid, BitrunExitComponent component, InteractHandEvent args)
    {
        if (args.Handled || !TryGetObjective(uid, args.User, out _))
            return;

        args.Handled = true;
        Disconnect(args.User, false);
    }

    private void NotifyAvatars(Run run, string message)
    {
        foreach (var avatar in run.Avatars)
        {
            if (!TerminatingOrDeleted(avatar))
                _popup.PopupEntity(message, avatar, avatar);
        }
    }
}
