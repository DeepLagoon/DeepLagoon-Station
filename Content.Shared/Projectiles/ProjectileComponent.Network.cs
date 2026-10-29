using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Projectiles;

public sealed partial class ProjectileComponent
{
    // Retain the generated state's name and fields for serialization compatibility.
    [Serializable, NetSerializable]
    public sealed class ProjectileComponent_AutoState : IComponentState
    {
        public Angle Angle;
        public NetEntity? Shooter;
        public NetEntity? Weapon;
        public bool IgnoreShooter;
    }
}

public sealed class ProjectileNetworkSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProjectileComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<ProjectileComponent, ComponentHandleState>(OnHandleState);
    }

    private void OnGetState(EntityUid uid, ProjectileComponent component, ref ComponentGetState args)
    {
        args.State = new ProjectileComponent.ProjectileComponent_AutoState
        {
            Angle = component.Angle,
            Shooter = TryGetNetEntity(component.Shooter, out var shooter) ? shooter : null,
            Weapon = TryGetNetEntity(component.Weapon, out var weapon) ? weapon : null,
            IgnoreShooter = component.IgnoreShooter,
        };
    }

    private void OnHandleState(EntityUid uid, ProjectileComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not ProjectileComponent.ProjectileComponent_AutoState state)
            return;

        component.Angle = state.Angle;
        component.Shooter = EnsureEntity<ProjectileComponent>(state.Shooter, uid);
        component.Weapon = EnsureEntity<ProjectileComponent>(state.Weapon, uid);
        component.IgnoreShooter = state.IgnoreShooter;
    }
}
