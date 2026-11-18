using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Shared.Interaction;

namespace Content.Server._DeepLagoon.NPC;

public sealed partial class ReplaceLightBulbOperator : HTNOperator
{
    [Dependency] private IEntityManager _entManager = default!;
    private CleanbotLightReplacerSystem _lights = default!;
    private SharedInteractionSystem _interaction = default!;

    [DataField("targetKey", required: true)]
    public string TargetKey = string.Empty;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _lights = sysManager.GetEntitySystem<CleanbotLightReplacerSystem>();
        _interaction = sysManager.GetEntitySystem<SharedInteractionSystem>();
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var fixture, _entManager) ||
            _entManager.Deleted(fixture))
        {
            return HTNOperatorStatus.Failed;
        }

        if (!_interaction.InRangeUnobstructed(owner, fixture))
            return HTNOperatorStatus.Failed;

        return _lights.TryReplace(owner, fixture)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
