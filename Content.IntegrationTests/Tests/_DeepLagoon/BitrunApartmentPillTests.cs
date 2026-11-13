using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Nutrition.Components;
using Content.Server.Nutrition.EntitySystems;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle.Components;
using Content.Shared.Inventory;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunApartmentPillTests
{
    [Test]
    public async Task PillsPutTheGroupToSleepAndReturnItThroughTheElevator()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var stop = await pair.CreateTestMap();
        var server = pair.Server;
        var sessions = await server.AddDummySessions(4);
        var bodies = new EntityUid[sessions.Length];
        var uniforms = new EntityUid[sessions.Length];
        EntityUid apartment = default, outsider = default, blue = default, red = default;
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var map = entities.System<SharedMapSystem>();
            for (var x = -2; x <= 2; x++)
            for (var y = -5; y <= 1; y++)
                map.SetTile(stop.Grid, new Vector2i(x, y), stop.Tile.Tile);

            var panel = new EntityCoordinates(stop.Grid.Owner, .5f, .5f);
            entities.SpawnEntity("DLApartmentBusStop", panel);
            entities.SpawnEntity("DLApartmentElevatorPanel", panel);
            entities.SpawnEntity("Airlock", panel.Offset(new Vector2(0, -4)));
            outsider = entities.SpawnEntity("MobHuman", new EntityCoordinates(stop.Grid.Owner, 2.5f, .5f));

            var apartments = entities.System<ApartmentSystem>();
            var inventory = entities.System<InventorySystem>();
            var owner = sessions[0].UserId;
            Assert.That(apartments.GrantTemplate(owner, "DLApartmentBitrun", out _), Is.Null);
            for (var index = 0; index < sessions.Length; index++)
            {
                var session = sessions[index];
                server.PlayerMan.SetStatus(session, SessionStatus.InGame);
                bodies[index] = entities.SpawnEntity("MobHuman", stop.GridCoords);
                server.PlayerMan.SetAttachedEntity(session, bodies[index]);
                uniforms[index] = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", stop.GridCoords);
                Assert.That(inventory.TryEquip(bodies[index], uniforms[index], "jumpsuit", force: true), Is.True);
                Assert.That(apartments.Enter(session, owner, out var message, "DLApartmentBitrun"), Is.True, message);
            }

            apartment = apartments.Instances[owner].Grid;
            foreach (var pill in entities.EntityQuery<BitrunApartmentPillComponent>())
            {
                if (entities.GetComponent<TransformComponent>(pill.Owner).GridUid != apartment)
                    continue;
                if (pill.ReturnToStop) red = pill.Owner;
                else blue = pill.Owner;
            }
            Assert.That(blue.IsValid() && red.IsValid(), Is.True);
            Feed(blue);
        });

        await server.WaitRunTicks(server.Timing.TickRate * 2);
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            Assert.That(entities.EntityExists(blue), Is.False);
            Assert.That(entities.HasComponent<SleepingComponent>(outsider), Is.False);
            foreach (var body in bodies)
            {
                var transform = entities.GetComponent<TransformComponent>(body);
                Assert.That(transform.GridUid, Is.EqualTo(apartment));
                Assert.That(entities.HasComponent<SleepingComponent>(body), Is.True);
                var position = entities.System<SharedTransformSystem>().GetWorldPosition(body);
                var local = Vector2.Transform(position, entities.GetComponent<TransformComponent>(apartment).InvWorldMatrix);
                Assert.That(local.Y, Is.EqualTo(5.5f).Within(.35f));
                Assert.That(new[] { 7.5f, 9.5f }.Any(x => MathF.Abs(x - local.X) < .35f), Is.True);
            }

            Assert.That(entities.System<SleepingSystem>().TryWaking(bodies[0]), Is.True);
            Feed(red);
        });

        await server.WaitRunTicks(server.Timing.TickRate * 2);
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            Assert.That(entities.EntityExists(red), Is.False);
            foreach (var body in bodies)
            {
                var transform = entities.GetComponent<TransformComponent>(body);
                Assert.That(transform.ParentUid, Is.EqualTo(apartment));
                Assert.That(transform.LocalPosition.Y, Is.InRange(11.5f, 13.5f));
                Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.True);
                Assert.That(entities.GetComponent<BuckleComponent>(body).Buckled, Is.False);
            }
        });

        await server.WaitRunTicks(server.Timing.TickRate * 14);
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var apartments = entities.System<ApartmentSystem>();
            var inventory = entities.System<InventorySystem>();
            for (var index = 0; index < sessions.Length; index++)
            {
                var body = bodies[index];
                Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(stop.Grid.Owner));
                Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.False);
                Assert.That(apartments.TryGetVisit(sessions[index].UserId, out _, out _), Is.False);
                Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var item), Is.True);
                Assert.That(item, Is.EqualTo(uniforms[index]));
                server.PlayerMan.SetAttachedEntity(sessions[index], null);
                entities.DeleteEntity(body);
            }
        });

        await server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();

        void Feed(EntityUid pill)
        {
            var entities = server.EntMan;
            var body = bodies[0];
            var transforms = entities.System<SharedTransformSystem>();
            var location = transforms.GetWorldPosition(body);
            var local = Vector2.Transform(location, entities.GetComponent<TransformComponent>(apartment).InvWorldMatrix);
            transforms.SetCoordinates(pill, new EntityCoordinates(apartment, local));
            Assert.That(entities.System<FoodSystem>().TryFeed(body, body, pill, entities.GetComponent<FoodComponent>(pill)).Success, Is.True);
        }
    }
}
