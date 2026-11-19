using Content.Server.Ghost.Roles.Events;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;
using Content.Server.NPC.HTN;
using Content.Shared.Clothing.Components;
using Content.Shared.Ghost.Roles.Components;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Server.Mind;
using Content.Shared.Ghost;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunDomainGuestComponent : Component
{
    [DataField] public bool Islander;
    [DataField] public bool Antagonist;
    [DataField] public bool DeathCounted;
}

[RegisterComponent]
public sealed partial class BitrunTemporaryRoleComponent : Component
{
    [DataField] public EntityUid PreviousMind;
    [DataField] public EntityUid? TemporaryMind;
    [DataField] public NetUserId User;
}

public sealed partial class BitrunSystem
{
    [Dependency] private NpcFactionSystem _npcFactions = default!;

    private void InitializeGuests()
    {
        SubscribeLocalEvent<BitrunDomainGuestComponent, GhostRoleSpawnerUsedEvent>(OnDomainGuestClaimed);
        SubscribeLocalEvent<BitrunDomainGuestComponent, MobStateChangedEvent>(OnDomainGuestState);
        SubscribeLocalEvent<BitrunDomainGuestComponent, MapInitEvent>(OnDomainGuestInit);
        SubscribeLocalEvent<BitrunDomainGuestComponent, GhostRoleMindTransferEvent>(CaptureGuestMind);
        SubscribeLocalEvent<BitrunGlitchComponent, GhostRoleMindTransferEvent>(CaptureGlitchMind);
        SubscribeLocalEvent<BitrunTemporaryRoleComponent, MindAddedMessage>(OnTemporaryMindAdded);
        SubscribeLocalEvent<BitrunTemporaryRoleComponent, EntityTerminatingEvent>(OnTemporaryRoleTerminating, before: [typeof(MindSystem)]);
        SubscribeLocalEvent<BitrunTemporaryRoleComponent, MobStateChangedEvent>(OnTemporaryRoleDeath);
    }

    private void CaptureGuestMind(EntityUid uid, BitrunDomainGuestComponent component, GhostRoleMindTransferEvent args) => CaptureTemporaryMind(uid, args);
    private void CaptureGlitchMind(EntityUid uid, BitrunGlitchComponent component, GhostRoleMindTransferEvent args) => CaptureTemporaryMind(uid, args);

    private void CaptureTemporaryMind(EntityUid uid, GhostRoleMindTransferEvent args)
    {
        if (!_mind.TryGetMind(args.Player.UserId, out var previous, out _))
            return;
        var temporary = EnsureComp<BitrunTemporaryRoleComponent>(uid);
        temporary.PreviousMind = previous.Value;
        temporary.User = args.Player.UserId;
    }

    private void OnTemporaryMindAdded(EntityUid uid, BitrunTemporaryRoleComponent component, MindAddedMessage args) => component.TemporaryMind = args.Mind.Owner;

    private void OnTemporaryRoleTerminating(Entity<BitrunTemporaryRoleComponent> ent, ref EntityTerminatingEvent args) => RestoreTemporaryMind(ent);

    private void OnTemporaryRoleDeath(Entity<BitrunTemporaryRoleComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            RestoreTemporaryMind(ent);
    }

    private void RestoreTemporaryMind(Entity<BitrunTemporaryRoleComponent> ent)
    {
        if (ent.Comp.TemporaryMind is not { } temporary ||
            !_mind.TryGetMind(ent.Comp.User, out var current, out _) || current != temporary ||
            !TryComp<MindComponent>(ent.Comp.PreviousMind, out var previous))
            return;
        ent.Comp.TemporaryMind = null;
        _mind.SetUserId(ent.Comp.PreviousMind, ent.Comp.User, previous);
        if (previous.CurrentEntity is not { } body || TerminatingOrDeleted(body))
            _mind.TransferTo(ent.Comp.PreviousMind, null, ghostCheckOverride: true, mind: previous);
        else if (!HasComp<GhostComponent>(body) && _mobState.IsDead(body))
            _ghosts.OnGhostAttempt(ent.Comp.PreviousMind, true, mind: previous);
    }

    private void OnDomainGuestInit(Entity<BitrunDomainGuestComponent> guest, ref MapInitEvent args)
    {
        if (guest.Comp.Antagonist)
            MakeDomainNpcFriendly(guest);
    }

    // WhiteMoon's npc_friendly adds the hostile wildlife factions to domain
    // actors. Keep the actors friendly to each faction without changing NPC AI
    // or the faction membership of bitrunners.
    private void MakeDomainNpcFriendly(EntityUid actor)
    {
        _npcFactions.ClearFactions(actor);
        foreach (var faction in new[] { "SimpleHostile", "Syndicate", "SyndicateNF", "PirateNF", "Xeno", "Dragon", "Zombie", "BitrunHeretic" })
            _npcFactions.AddFaction(actor, faction);
        if (Transform(actor).MapUid is { } map)
        {
            foreach (var npc in FindMapEntities<HTNComponent>(map))
                _npcFactions.IgnoreEntity(npc, actor);
        }
    }

    private ProtoId<StartingGearPrototype>? DefenderGear(Run run)
    {
        var choices = new List<ProtoId<StartingGearPrototype>>();
        foreach (var spawner in FindMapEntities<GhostRoleMobSpawnerComponent>(run.Map))
        {
            if (Comp<GhostRoleMobSpawnerComponent>(spawner).Prototype is not { } body)
                continue;
            var prototype = _prototypes.Index(body);
            if (!prototype.TryGetComponent<BitrunDomainGuestComponent>(out var guest) || !guest.Antagonist ||
                !prototype.TryGetComponent<LoadoutComponent>(out var loadout) || loadout.StartingGear == null)
                continue;
            choices.AddRange(loadout.StartingGear);
        }
        if (choices.Count == 0)
            return null;
        return _random.Pick(choices);
    }

    private void OnDomainGuestClaimed(Entity<BitrunDomainGuestComponent> guest, ref GhostRoleSpawnerUsedEvent args)
    {
        if (Transform(guest).MapUid is not { } map || !_domains.TryGetValue(map, out var run) || run.Completed)
        {
            QueueDel(guest);
            return;
        }
        EnsureComp<BitrunVirtualEntityComponent>(guest).Domain = map;
        run.Threats.Add(guest);
        _popup.PopupEntity(Loc.GetString("bitrun-guest-domain-bound"), guest, guest);
    }

    private void OnDomainGuestState(Entity<BitrunDomainGuestComponent> guest, ref MobStateChangedEvent args)
    {
        if (!guest.Comp.Islander || guest.Comp.DeathCounted || args.NewMobState != MobState.Dead ||
            Transform(guest).MapUid is not { } map || !_domains.TryGetValue(map, out var run) ||
            run.Domain.Objective != BitrunObjective.Island)
            return;
        guest.Comp.DeathCounted = true;
        AdvanceObjective(run, 1, Transform(guest).Coordinates);
    }
}
