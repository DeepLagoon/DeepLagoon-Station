using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunGunFiringTests
{
    [Test]
    public async Task ClientRejectsTheSameUncycledRifleShotAsTheServer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        EntityUid user = default, gun = default;
        await pair.Server.WaitAssertion(() =>
        {
            user = server.SpawnEntity("MobHuman", map.GridCoords);
            gun = server.SpawnEntity("BitrunSourceObjItemGunBallisticRifleBoltaction", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            Assert.That(server.System<SharedHandsSystem>().TryPickup(user, gun), Is.True);
            Assert.That(server.System<SharedInteractionSystem>().UseInHandInteraction(user, gun), Is.True);
            Assert.That(server.GetComponent<WieldableComponent>(gun).Wielded, Is.True);
            Assert.That(server.GetComponent<BitrunSourceGunComponent>(gun).BoltOpen, Is.False,
                "Taking the rifle in both hands must not also open its bolt.");
            server.System<SharedCombatModeSystem>().SetInCombatMode(user, true);
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            server.System<GunSystem>().AttemptShoot(user, gun, server.GetComponent<GunComponent>(gun), map.GridCoords.Offset(new Vector2(8, 0)));
            Assert.That(server.GetComponent<BitrunSourceGunComponent>(gun).NeedsCycle, Is.True);
            var attempt = new ShotAttemptedEvent { User = user, Used = (gun, server.GetComponent<GunComponent>(gun)) };
            server.EventBus.RaiseLocalEvent(gun, ref attempt);
            Assert.That(attempt.Cancelled, Is.True);
        });
        await pair.RunSeconds(0.5f);
        var netGun = server.GetNetEntity(gun);
        var netUser = server.GetNetEntity(user);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var clientGun = client.GetEntity(netGun);
            var attempt = new ShotAttemptedEvent { User = client.GetEntity(netUser), Used = (clientGun, client.GetComponent<GunComponent>(clientGun)) };
            client.EventBus.RaiseLocalEvent(clientGun, ref attempt);
            Assert.That(attempt.Cancelled, Is.True, "An uncycled rifle must not play a predicted shot that the server rejects.");

            var sprites = client.System<Robust.Client.GameObjects.SpriteSystem>();
            var appearance = client.System<Robust.Client.GameObjects.AppearanceSystem>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(p =>
                         p.ID.StartsWith("Bitrun") && p.Components.ContainsKey("CartridgeAmmo") &&
                         p.Components.ContainsKey("SpentAmmoVisuals")))
            {
                var cartridge = client.SpawnEntity(prototype.ID, client.GetComponent<TransformComponent>(clientGun).Coordinates);
                var sprite = new Entity<Robust.Client.GameObjects.SpriteComponent>(cartridge,
                    client.GetComponent<Robust.Client.GameObjects.SpriteComponent>(cartridge));
                Assert.That(sprites.TryGetLayer(sprite, Content.Client.Weapons.Ranged.Components.AmmoVisualLayers.Base, out var layer, true), Is.True, prototype.ID);
                var data = new Entity<AppearanceComponent>(cartridge, client.GetComponent<AppearanceComponent>(cartridge));
                foreach (var spent in new[] { false, true })
                {
                    appearance.SetData(cartridge, Content.Shared.Weapons.Ranged.Systems.AmmoVisuals.Spent, spent);
                    appearance.OnChangeData(data, sprite.Comp);
                    Assert.That(layer.ActualRsi.TryGetState(layer.State, out _), Is.True,
                        $"{prototype.ID}: missing {(spent ? "spent" : "live")} cartridge sprite {layer.State}.");
                }
                client.DeleteEntity(cartridge);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EveryImportedGunFiresAProjectileThatTravelsOutsideItsContainer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        string[] prototypes = [];
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            prototypes = pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<EntityPrototype>()
                .Where(p => p.ID.StartsWith("BitrunSourceObjItemGun")).Select(p => p.ID).Order().ToArray();
            Assert.That(prototypes.Length, Is.GreaterThanOrEqualTo(30));
        });
        foreach (var prototype in prototypes)
        {
            EntityUid gun = default;
            EntityUid[] projectiles = [];
            Vector2[] starts = [];
            var readyAfter = 2.1f;
            await pair.Server.WaitAssertion(() =>
            {
                user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
                em.System<SharedCombatModeSystem>().SetInCombatMode(user, true);
                gun = em.SpawnEntity(prototype, map.GridCoords);
                Assert.That(em.System<SharedHandsSystem>().TryPickup(user, gun), Is.True, prototype);
                if (em.TryGetComponent<WieldableComponent>(gun, out var wieldable))
                    Assert.That(em.System<SharedWieldableSystem>().TryWield(gun, wieldable, user), Is.True, prototype);
                readyAfter = Math.Max(readyAfter,
                    (float) (em.GetComponent<GunComponent>(gun).NextFire - pair.Server.Timing.CurTime).TotalSeconds + 0.1f);
            });
            await pair.RunSeconds(readyAfter);
            await pair.Server.WaitAssertion(() =>
            {
                em.System<GunSystem>().AttemptShoot(user, gun, em.GetComponent<GunComponent>(gun), map.GridCoords.Offset(new Vector2(12.5f, 0.5f)));
                projectiles = em.EntityQuery<ProjectileComponent>().Where(p => p.Weapon == gun).Select(p => p.Owner).ToArray();
                Assert.That(projectiles, Is.Not.Empty,
                    $"{prototype}: now={pair.Server.Timing.CurTime}, nextFire={em.GetComponent<GunComponent>(gun).NextFire}, " +
                    $"rate={em.GetComponent<GunComponent>(gun).FireRateModified}, wielded={em.GetComponentOrNull<WieldableComponent>(gun)?.Wielded}");
                starts = projectiles.Select(p => em.System<SharedTransformSystem>().GetMapCoordinates(p).Position).ToArray();
                foreach (var projectile in projectiles)
                {
                    Assert.That(em.GetComponent<PhysicsComponent>(projectile).LinearVelocity.Length(), Is.GreaterThan(1), prototype);
                    Assert.That(em.System<SharedContainerSystem>().IsEntityInContainer(projectile), Is.False, prototype);
                }
            });
            await pair.RunSeconds(0.05f);
            await pair.Server.WaitAssertion(() =>
            {
                for (var i = 0; i < projectiles.Length; i++)
                {
                    Assert.That(em.EntityExists(projectiles[i]), Is.True, $"{prototype}: projectile vanished before travelling.");
                    Assert.That(Vector2.Distance(starts[i], em.System<SharedTransformSystem>().GetMapCoordinates(projectiles[i]).Position),
                        Is.GreaterThan(0.1f), prototype);
                    em.DeleteEntity(projectiles[i]);
                }
                em.DeleteEntity(gun);
                em.DeleteEntity(user);
            });
        }
        await pair.CleanReturnAsync();
    }

    [TestCase("BitrunSourceObjItemGunBallisticRifleBoltaction")]
    [TestCase("BitrunMapItemGunBallisticRifleBoltactionSpriteOffsetMinus0Point250Point25")]
    [TestCase("BitrunSourceObjItemGunBallisticRifleLionhunter")]
    public async Task HeldHuntingRifleFiresMovingBulletHitsTargetAndCycles(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid user = default, gun = default, target = default, projectile = default;
        var firstDamage = 0f;
        await pair.Server.WaitAssertion(() =>
        {
            var grid = new Entity<MapGridComponent>(map.Grid, em.GetComponent<MapGridComponent>(map.Grid));
            for (var x = -2; x < 14; x++)
            for (var y = -3; y < 4; y++)
                em.System<SharedMapSystem>().SetTile(grid, new Vector2i(x, y), map.Tile.Tile);
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
            gun = em.SpawnEntity(prototype, map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickup(user, gun), Is.True);
            Assert.That(em.System<SharedInteractionSystem>().UseInHandInteraction(user, gun), Is.True);
            Assert.That(em.GetComponent<WieldableComponent>(gun).Wielded, Is.True);
            Assert.That(em.GetComponent<BitrunSourceGunComponent>(gun).BoltOpen, Is.False,
                "Taking the rifle in both hands must not also open its bolt.");
            em.System<SharedCombatModeSystem>().SetInCombatMode(user, true);
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            em.System<GunSystem>().AttemptShoot(user, gun, em.GetComponent<GunComponent>(gun), em.GetComponent<TransformComponent>(target).Coordinates);
            projectile = em.EntityQuery<ProjectileComponent>().Single(p => p.Weapon == gun).Owner;
            Assert.That(em.GetComponent<PhysicsComponent>(projectile).LinearVelocity.Length(), Is.GreaterThan(1));
            Assert.That(em.System<SharedContainerSystem>().IsEntityInContainer(projectile), Is.False);
        });
        await pair.RunSeconds(0.6f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Positive,
                "The actual projectile must travel from the held rifle and damage the target.");
            firstDamage = em.GetComponent<DamageableComponent>(target).TotalDamage.Float();
            Assert.That(em.GetComponent<BitrunSourceGunComponent>(gun).NeedsCycle, Is.True);
            em.System<SharedInteractionSystem>().UseInHandInteraction(user, gun);
            Assert.That(em.GetComponent<BitrunSourceGunComponent>(gun).BoltOpen, Is.True);
            Assert.That(em.EntityQuery<CartridgeAmmoComponent>().Any(c => c.Spent &&
                !em.System<SharedContainerSystem>().IsEntityInContainer(c.Owner)), Is.True,
                "Opening the bolt ejects the fired round.");
        });
        await pair.RunSeconds(1.1f);
        await pair.Server.WaitAssertion(() =>
        {
            em.System<SharedInteractionSystem>().UseInHandInteraction(user, gun);
            Assert.That(em.GetComponent<BitrunSourceGunComponent>(gun).BoltOpen, Is.False);
            Assert.That(em.GetComponent<BitrunSourceGunComponent>(gun).NeedsCycle, Is.False);
        });
        await pair.RunSeconds(1.1f);
        await pair.Server.WaitAssertion(() =>
            em.System<GunSystem>().AttemptShoot(user, gun, em.GetComponent<GunComponent>(gun), em.GetComponent<TransformComponent>(target).Coordinates));
        await pair.RunSeconds(0.6f);
        await pair.Server.WaitAssertion(() =>
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.GreaterThan(firstDamage),
                "Closing the bolt must make the next live round fire and damage the target."));
        await pair.CleanReturnAsync();
    }
}
