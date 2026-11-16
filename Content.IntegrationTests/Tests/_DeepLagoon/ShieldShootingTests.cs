using System.Numerics;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShieldShootingTests
{
    [TestCase("WeaponDisabler", true)]
    [TestCase("WeaponEnergyShotgun", false)]
    public async Task ShootingWithShieldObeysWeaponWieldRequirement(string weapon, bool shouldFire)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            var shooter = entities.SpawnEntity("MobHuman", coords);
            var gun = entities.SpawnEntity(weapon, coords);
            var shield = entities.SpawnEntity("RiotShield", coords);
            var hands = entities.System<SharedHandsSystem>();
            var guns = entities.System<GunSystem>();

            Assert.That(hands.TryPickup(shooter, gun), Is.True);
            Assert.That(hands.TryPickupAnyHand(shooter, shield), Is.True);
            Assert.That(guns.TryGetGun(shooter, out var activeGun), Is.True);
            Assert.That(activeGun.Owner, Is.EqualTo(gun));

            var before = new GetAmmoCountEvent();
            entities.EventBus.RaiseLocalEvent(gun, ref before);
            Assert.That(before.Count, Is.Positive);

            guns.AttemptShoot(shooter, gun, activeGun.Comp, coords.Offset(Vector2.UnitX));

            var after = new GetAmmoCountEvent();
            entities.EventBus.RaiseLocalEvent(gun, ref after);
            Assert.That(after.Count, Is.EqualTo(shouldFire ? before.Count - 1 : before.Count));
        });

        await pair.CleanReturnAsync();
    }
}
