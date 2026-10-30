using System.Linq;
using System.Numerics;
using Content.Shared.Atmos.Components;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShipShieldPredictionTests
{
    [Test]
    public async Task PredictedHumanCanOverlapSeveralShieldEdgesWithoutContacts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        NetEntity netActor = default, netShield = default;
        EntityUid actor = default, shield = default;
        var center = new Vector2(.5f, .5f);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(map.Grid.Owner, map.Grid.Comp, Enumerable.Range(-4, 9)
                .SelectMany(x => Enumerable.Range(-4, 9).Select(y => (new Vector2i(x, y), floor))).ToList());
            shield = entities.SpawnEntity("ShipShield", new EntityCoordinates(map.Grid.Owner, center));
            var body = entities.EnsureComponent<PhysicsComponent>(shield);
            var physics = entities.System<SharedPhysicsSystem>();
            physics.SetBodyType(shield, BodyType.Static, body: body);
            var chain = new ChainShape();
            chain.CreateLoop(Vector2.Zero, 2f);
            Assert.That(entities.System<FixtureSystem>().TryCreateFixture(shield, chain, "shield", hard: false,
                collisionLayer: (int) CollisionGroup.BulletImpassable, body: body), Is.True);
            physics.SetCanCollide(shield, true, body: body);
            actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, center + new Vector2(2, 0)));
            netActor = entities.GetNetEntity(actor);
            netShield = entities.GetNetEntity(shield);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
        });
        // The replicated flammable sensor overlaps multiple proxies belonging to the same chain fixture.
        await pair.RunTicksSync(30);
        for (var i = 0; i < 3; i++)
        {
            await pair.Client.WaitPost(() =>
            {
                var entities = pair.Client.EntMan;
                entities.System<SharedPhysicsSystem>().RegenerateContacts(entities.GetEntity(netShield));
            });
            await pair.RunTicksSync(2);
        }
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var localActor = entities.GetEntity(netActor);
            var localShield = entities.GetEntity(netShield);
            var flammable = entities.GetComponent<FlammableComponent>(localActor);
            var fixtures = entities.GetComponent<FixturesComponent>(localActor);
            Assert.That(fixtures.Fixtures.ContainsKey(flammable.FlammableFixtureID), Is.True);
            Assert.That(entities.GetComponent<FixturesComponent>(localShield).Fixtures["shield"].ProxyCount, Is.GreaterThan(1));
            Assert.That(entities.GetComponent<PhysicsComponent>(localShield).ContactCount, Is.Zero,
                "Non-projectiles pass through ship shields on the client as well as the server.");
        });
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<PhysicsComponent>(shield).ContactCount, Is.Zero);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
        });
        await pair.CleanReturnAsync();
    }
}
