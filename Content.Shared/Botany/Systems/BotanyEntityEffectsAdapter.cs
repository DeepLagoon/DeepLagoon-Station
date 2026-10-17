using Content.Shared.EntityEffects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Shared.EntityEffects;

// Compatibility with the newer botany effect dispatch. Existing chemistry and other gameplay
// retain their current effect types; only plant effects use this content-owned event adapter.
[ByRefEvent]
public struct EntityEffectEvent<T>(T effect, float scale) where T : EntityEffect
{
    public EntityUid? User;
    public T Effect = effect;
    public float Scale = scale;
}

public abstract partial class EntityEffectBase<T> : EntityEffect where T : EntityEffectBase<T>
{
    public override void Effect(EntityEffectBaseArgs args)
    {
        var ev = new EntityEffectEvent<T>((T)this, args is EntityEffectReagentArgs reagent ? reagent.Scale.Float() : 1f);
        args.EntityManager.EventBus.RaiseLocalEvent(args.TargetEntity, ref ev);
    }

    public virtual string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => EntityEffectGuidebookText(prototype, entSys);
}

public abstract partial class EntityEffectSystem<TComp, TEffect> : EntitySystem
    where TComp : Component where TEffect : EntityEffect
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TComp, EntityEffectEvent<TEffect>>(OnEffect);
    }
    private void OnEffect(EntityUid uid, TComp component, ref EntityEffectEvent<TEffect> args) => Effect((uid, component), ref args);
    protected abstract void Effect(Entity<TComp> entity, ref EntityEffectEvent<TEffect> args);
}

public sealed partial class SharedEntityEffectsSystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private IRobustRandom _random = default!;

    public bool TryApplyEffect(EntityUid target, EntityEffect effect, float scale = 1f, EntityUid? user = null)
    {
        if (!_net.IsServer || TerminatingOrDeleted(target))
            return false;
        var args = new EntityEffectReagentArgs(target, EntityManager, null, null,
            Content.Shared.FixedPoint.FixedPoint2.New(scale), null, null, Content.Shared.FixedPoint.FixedPoint2.New(scale));
        if (!effect.ShouldApply(args, _random))
            return false;
        effect.Effect(args);
        return true;
    }

    public void ApplyEffects(EntityUid target, EntityEffect[] effects, float scale = 1f)
    {
        foreach (var effect in effects)
            TryApplyEffect(target, effect, scale);
    }
}
