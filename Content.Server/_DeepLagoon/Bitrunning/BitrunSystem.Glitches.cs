using System.Linq;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Ghost.Roles.Events;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Emag.Systems;
using Content.Shared.Interaction;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunThreatComponent : Component
{
    public int Threat;
}

[RegisterComponent]
public sealed partial class BitrunGlitchSpawnerComponent : Component;

[RegisterComponent]
public sealed partial class BitrunGlitchComponent : Component;

public sealed partial class BitrunSystem
{
    [Dependency] private EmagSystem _emag = default!;

    private const int MaximumGlitchThreat = 120;
    private const int MinimumGlitchChance = 5;
    private const int MaximumGlitchChance = 35;
    private const float GlitchSafehouseDistance = 6f;
    private const float GlitchGoalDistance = 3f;

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
        var chance = Math.Clamp(MinimumGlitchChance + threat.Threat / 3, MinimumGlitchChance, MaximumGlitchChance);
        if (!_random.Prob(chance / 100f))
            return;

        var candidates = run.Scenarios
            .Where(uid => !TerminatingOrDeleted(uid) && MetaData(uid).EntityName is "mob_segment" or "boss")
            .Concat(run.CacheSpawns)
            .Distinct()
            .Where(uid => !TerminatingOrDeleted(uid) &&
                          run.Spawns.All(spawn => !IsNearby(uid, spawn, GlitchSafehouseDistance)) &&
                          run.Goals.All(goal => !IsNearby(uid, goal, GlitchGoalDistance)))
            .ToArray();
        if (candidates.Length == 0)
            return;

        var role = _random.Pick(GlitchRoles.Where(role => role.Threat <= threat.Threat).ToArray());
        var anchor = _random.Pick(candidates);
        Spawn(role.Spawner, Transform(anchor).Coordinates);
        threat.Threat = Math.Max(0, threat.Threat - role.Threat / 2);
    }

    private void IncreaseGlitchThreat(Run run, string grade)
    {
        if (run.Domain.TestOnly)
            return;

        foreach (var spawner in FindMapEntities<BitrunGlitchSpawnerComponent>(run.Map))
        {
            RemComp<GhostRoleComponent>(spawner);
            QueueDel(spawner);
        }

        var threat = EnsureComp<BitrunThreatComponent>(run.Server);
        var score = grade switch
        {
            "S" => 18,
            "A" => 13,
            "B" => 9,
            _ => 5,
        };
        threat.Threat = Math.Min(MaximumGlitchThreat, threat.Threat + score + run.Domain.Difficulty * 2);
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

        Spawn(GlitchBurst, xform.Coordinates);
        NotifyAvatars(run, Loc.GetString("bitrun-glitch-detected", ("role", MetaData(uid).EntityName)));
        _popup.PopupEntity(Loc.GetString("bitrun-glitch-upload-hint"), uid, uid);
    }
}
