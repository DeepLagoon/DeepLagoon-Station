using System.Numerics;
using Content.Server._DeepLagoon.Weapons;

namespace Content.Server.Weapons.Ranged.Systems;

public sealed partial class GunSystem
{
    [Dependency] private LaserProjectileWindowSystem _laserWindows = default!;

    public override void ShootProjectile(EntityUid uid, Vector2 direction, Vector2 gunVelocity, EntityUid gunUid,
        EntityUid? user = null, float speed = 20f, float offset = 0f)
    {
        _laserWindows.Track(uid);
        base.ShootProjectile(uid, direction, gunVelocity, gunUid, user, speed, offset);
    }
}
