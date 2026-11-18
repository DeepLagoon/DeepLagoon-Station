using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;

namespace Content.Server._DeepLagoon.NPC;

public sealed partial class PickBrokenLightOperator : HTNOperator
{
    [Dependency] private IEntityManager _entManager = default!;
    private EntityLookupSystem _lookup = default!;
    private CleanbotLightReplacerSystem _lights = default!;
    private PathfindingSystem _pathfinding = default!;

    [DataField("targetKey", required: true)]
    public string TargetKey = string.Empty;

    [DataField("targetMoveKey", required: true)]
    public string TargetMoveKey = string.Empty;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _lookup = sysManager.GetEntitySystem<EntityLookupSystem>();
        _lights = sysManager.GetEntitySystem<CleanbotLightReplacerSystem>();
        _pathfinding = sysManager.GetEntitySystem<PathfindingSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<CleanbotLightReplacerComponent>(owner, out var replacer))
            return (false, null);

        var ownerXform = _entManager.GetComponent<TransformComponent>(owner);
        var fixtures = new HashSet<Entity<PoweredLightComponent>>();
        _lookup.GetEntitiesInRange(ownerXform.Coordinates, replacer.Range, fixtures);

        foreach (var fixture in fixtures)
        {
            if (!_lights.CanReplace(owner, fixture.Owner, replacer, fixture.Comp))
                continue;

            var path = await _pathfinding.GetPath(owner, fixture.Owner,
                SharedInteractionSystem.InteractionRange - 1f, cancelToken);
            if (path.Result == PathResult.NoPath)
                continue;

            return (true, new Dictionary<string, object>
            {
                { TargetKey, fixture.Owner },
                { TargetMoveKey, _entManager.GetComponent<TransformComponent>(fixture.Owner).Coordinates },
                { NPCBlackboard.PathfindKey, path },
            });
        }

        return (false, null);
    }
}
