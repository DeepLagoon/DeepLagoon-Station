using System.Linq;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Ghost.Roles.Events;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Emag.Systems;
using Content.Shared.Interaction;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunThreatComponent : Component
{
    public int Threat;
}

[RegisterComponent]
public sealed partial class BitrunGlitchSpawnerComponent : Component
{
    [DataField] public EntityUid? Target;
}

[RegisterComponent]
public sealed partial class BitrunGlitchComponent : Component;

public sealed partial class BitrunSystem
{
    [Dependency] private EmagSystem _emag = default!;

    private const int MinimumGlitchChance = 5;
    private const int MaximumGlitchChance = 15;

    private static readonly EntProtoId GlitchBurst = "BitrunGlitchBurst";

    // Ordered by the WhiteMoon threat thresholds for each role.
    private static readonly (int Threat, EntProtoId Spawner)[] GlitchRoles =
    [
        (0, "BitrunGhostCyberPolice"),
        (50, "BitrunGhostCyberTac"),
        (90, "BitrunGhostNetguardian"),
    ];

    private void InitializeGlitches()
    {
        SubscribeLocalEvent<BitrunGlitchComponent, GhostRoleSpawnerUsedEvent>(OnGlitchClaimed);
        SubscribeLocalEvent<BitrunServerComponent, GotEmaggedEvent>(OnServerEmagged);
        SubscribeLocalEvent<BitrunCacheGoalComponent, InteractHandEvent>(OnGlitchUpload);
    }

    private void MaybeSpawnGlitch(Run run)
    {
        if (run.Domain.TestOnly)
            return;

        var threat = EnsureComp<BitrunThreatComponent>(run.Server);
        var emagged = _emag.CheckFlag(run.Server, EmagType.Interaction);
        var chance = Math.Clamp(threat.Threat * (emagged ? 0.4f : 0.2f), MinimumGlitchChance,
            MaximumGlitchChance * (emagged ? 2 : 1));
        if (!_random.Prob(chance / 100f))
            return;

        SpawnMutation(run, null);
    }

    private EntityUid[] MutationCandidates(Run run)
    {
        var reserved = FindMapEntities<BitrunGlitchSpawnerComponent>(run.Map)
            .Select(uid => Comp<BitrunGlitchSpawnerComponent>(uid).Target).ToHashSet();
        return FindMapEntities<MobStateComponent>(run.Map).Where(uid => !TerminatingOrDeleted(uid) &&
            !HasComp<BitrunAvatarComponent>(uid) && !HasComp<BitrunDomainGuestComponent>(uid) &&
            !HasComp<BitrunGlitchComponent>(uid) && !reserved.Contains(uid) &&
            !(run.Domain.Objective == BitrunObjective.Boss && run.Targets.Contains(uid)) &&
            !(TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind)).ToArray();
    }

    private bool SpawnMutation(Run run, EntProtoId? forcedRole)
    {
        var candidates = MutationCandidates(run);
        if (candidates.Length == 0)
            return false;

        var threat = EnsureComp<BitrunThreatComponent>(run.Server);
        var role = _random.Pick(GlitchRoles.Where(role => role.Threat <= threat.Threat).ToArray());
        var anchor = _random.Pick(candidates);
        var spawner = Spawn(forcedRole ?? role.Spawner, Transform(anchor).Coordinates);
        Comp<BitrunGlitchSpawnerComponent>(spawner).Target = anchor;
        if (forcedRole == null)
            threat.Threat = Math.Max(0, threat.Threat - role.Threat / 2);
        return true;
    }

    public bool SpawnGlitchEvent(EntProtoId? forcedRole = null)
    {
        var active = _runs.Values.Where(run => !run.Completed && !run.Domain.TestOnly && run.Domain.Difficulty > 0 &&
            GetSideCount(run, BitrunSide.Attackers) > 0 && IsOnline(run.Server) && MutationCandidates(run).Length > 0).ToArray();
        return active.Length > 0 && SpawnMutation(_random.Pick(active), forcedRole);
    }

    private void IncreaseGlitchThreat(Run run, TimeSpan elapsed)
    {
        if (run.Domain.TestOnly)
            return;

        foreach (var spawner in FindMapEntities<BitrunGlitchSpawnerComponent>(run.Map))
        {
            RemComp<GhostRoleComponent>(spawner);
            QueueDel(spawner);
        }

        var threat = EnsureComp<BitrunThreatComponent>(run.Server);
        threat.Threat = AddPoints(threat.Threat, ScoreRun(run, elapsed));
    }

    private void OnServerEmagged(Entity<BitrunServerComponent> ent, ref GotEmaggedEvent args)
    {
        if (!_emag.CompareFlag(args.Type, EmagType.Interaction) || _emag.CheckFlag(ent, EmagType.Interaction))
            return;

        args.Handled = true;
    }

    private void OnGlitchUpload(EntityUid goal, BitrunCacheGoalComponent component, InteractHandEvent args)
    {
        if (args.Handled || !HasComp<BitrunGlitchComponent>(args.User) ||
            Transform(goal).MapUid is not { } map || !_domains.TryGetValue(map, out var run) ||
            Transform(args.User).MapUid != map || !_interaction.InRangeUnobstructed(args.User, goal) ||
            !_emag.CheckFlag(run.Server, EmagType.Interaction) ||
            FindEquipment<BitrunForgeComponent>(run.Server, EquipmentRange) is not { } forge ||
            !_mind.TryGetMind(args.User, out var mindId, out var mind))
            return;

        var prototype = MetaData(args.User).EntityPrototype?.ID;
        if (prototype is not ("BitrunCyberPolice" or "BitrunCyberTac" or "BitrunNetguardian"))
            return;

        args.Handled = true;
        // Materialize a fresh copy from the allowed prototype. Its simulated inventory stays in the domain.
        var materialized = Spawn(prototype, Transform(forge).Coordinates);
        RemComp<BitrunGlitchComponent>(materialized);
        _mind.TransferTo(mindId, materialized, ghostCheckOverride: true, mind: mind);
        RemComp<BitrunTemporaryRoleComponent>(args.User);
        QueueDel(args.User);
        Spawn(GlitchBurst, Transform(forge).Coordinates);
        Log.Info($"Bitrun {run.Id} materialized glitch {prototype} at byteforge {forge}");
    }

    private void OnGlitchClaimed(EntityUid uid, BitrunGlitchComponent component, GhostRoleSpawnerUsedEvent args)
    {
        var xform = Transform(uid);
        if (!HasComp<BitrunGlitchSpawnerComponent>(args.Spawner) || xform.MapUid is not { } map ||
            !_domains.TryGetValue(map, out var run) || run.Completed)
        {
            QueueDel(uid);
            return;
        }

        if (Comp<BitrunGlitchSpawnerComponent>(args.Spawner).Target is { } target)
        {
            if (TerminatingOrDeleted(target) || Transform(target).MapUid != map ||
                TryComp<MindContainerComponent>(target, out var targetMind) && targetMind.HasMind)
            {
                QueueDel(uid);
                return;
            }
            _transform.SetCoordinates(uid, Transform(target).Coordinates);
            _transform.AttachToGridOrMap(uid);
            QueueDel(target);
        }

        Spawn(GlitchBurst, xform.Coordinates);
        run.Threats.Add(uid);
        MakeDomainNpcFriendly(uid);
        NotifyAvatars(run, Loc.GetString("bitrun-glitch-detected", ("role", MetaData(uid).EntityName)));
        _popup.PopupEntity(Loc.GetString("bitrun-glitch-upload-hint"), uid, uid);
    }
}
