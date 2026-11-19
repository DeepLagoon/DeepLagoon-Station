using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Bitrunning;

/// <summary>Source X-ray bolts pass through scenery while still hitting mobs.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class BitrunProjectilePhaseComponent : Component;

public sealed class BitrunProjectilePhaseSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunProjectilePhaseComponent, ProjectileHitEvent>(OnHit);
    }

    private void OnHit(Entity<BitrunProjectilePhaseComponent> projectile, ref ProjectileHitEvent args)
    {
        if (!HasComp<MobStateComponent>(args.Target))
            args.Handled = true;
    }
}
