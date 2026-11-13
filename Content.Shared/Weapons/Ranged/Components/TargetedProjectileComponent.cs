using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Ranged.Components;

[RegisterComponent, NetworkedComponent]
[Access(typeof(SharedGunSystem), typeof(TargetedProjectileNetworkSystem))]
public sealed partial class TargetedProjectileComponent : Component
{
    [DataField]
    public EntityUid Target;

    // Preserve the generated state's name and wire field.
    [Serializable, NetSerializable]
    public sealed class TargetedProjectileComponent_AutoState : IComponentState
    {
        public NetEntity Target;
    }
}

public sealed class TargetedProjectileNetworkSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<TargetedProjectileComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<TargetedProjectileComponent, ComponentHandleState>(OnHandleState);
    }

    private void OnGetState(EntityUid uid, TargetedProjectileComponent component, ref ComponentGetState args)
    {
        args.State = new TargetedProjectileComponent.TargetedProjectileComponent_AutoState
        {
            Target = TryGetNetEntity(component.Target, out var target) ? target.Value : NetEntity.Invalid,
        };
    }

    private void OnHandleState(EntityUid uid, TargetedProjectileComponent component, ref ComponentHandleState args)
    {
        if (args.Current is TargetedProjectileComponent.TargetedProjectileComponent_AutoState state)
            component.Target = EnsureEntity<TargetedProjectileComponent>(state.Target, uid);
    }
}
