using Content.Shared.Movement.Systems;

namespace Content.Shared.Mood;

public sealed partial class SharedMoodSystem : EntitySystem
{
    [Dependency] private SharedJetpackSystem _jetpack = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NetMoodComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMoveSpeed);
    }

    private void OnRefreshMoveSpeed(EntityUid uid, NetMoodComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        if (!_jetpack.IsUserFlying(uid))
            args.ModifySpeed(1f, component.SprintSpeedModifier);
    }
}
