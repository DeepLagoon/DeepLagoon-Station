using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Teleportation;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Interaction.Events;
using Content.Shared.Stacks;
using Content.Shared.Teleportation;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentIsolationTests
{
    [Test]
    public async Task TeleportsRejectBothDirectionsAndDoNotConsumeCrystals()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var home = apartments.PrepareElevatorApartment(session.UserId);
            var inside = new EntityCoordinates(home.Grid, new Vector2(home.Template.Arrival.X + .5f, home.Template.Arrival.Y + .5f));
            var body = entities.SpawnEntity("MobHuman", inside);
            var other = entities.SpawnEntity("MobHuman", outside.GridCoords);
            var transform = entities.System<SharedTransformSystem>();
            var policy = entities.System<ApartmentTeleportSystem>();
            Assert.That(policy.CanTeleport(other, outside.GridCoords), Is.True);
            Assert.That(policy.CanTeleport(body), Is.False);
            Assert.That(policy.CanTeleport(body, inside), Is.False);
            Assert.That(policy.CanTeleport(body, outside.GridCoords), Is.False);
            Assert.That(policy.CanTeleport(other, inside), Is.False);
            Assert.That(policy.CanTeleport(other, transform.ToMapCoordinates(inside)), Is.False);
            var start = entities.GetComponent<TransformComponent>(body).Coordinates;
            entities.System<TeleportSystem>().RandomTeleport(body, new TeleportSpecifier());
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(start));
            var crystal = entities.SpawnEntity("Paper", inside);
            var crystalComponent = entities.AddComponent<RandomTeleportOnUseComponent>(crystal);
            crystalComponent.Specifier = new TeleportSpecifier();
            var use = new UseInHandEvent(body);
            entities.EventBus.RaiseLocalEvent(crystal, use);
            Assert.That(entities.EntityExists(crystal), Is.True, "Blocked teleport must not consume the item.");
            Assert.That(entities.GetComponent<TransformComponent>(body).Coordinates, Is.EqualTo(start));
            foreach (var definition in pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<ApartmentFurniturePrototype>()
                         .Where(item => item.ID.StartsWith("DLAptUtility")))
            {
                var item = entities.SpawnEntity(definition.Entity, inside);
                if (entities.TryGetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(item, out var receiver))
                    Assert.That(receiver.NeedsPower, Is.False, definition.ID);
                entities.DeleteEntity(item);
            }
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            transform.SetCoordinates(body, outside.GridCoords);
            Assert.That(apartments.Enter(session, session.UserId, out var message, returnPoint: outside.GridCoords), Is.True, message);
            transform.SetCoordinates(body, outside.GridCoords);
            apartments.Update(0);
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(home.Grid), "An unhandled displacement must not leave a resident outside their apartment.");
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(crystal);
            entities.DeleteEntity(body);
            entities.DeleteEntity(other);
        });
        await pair.CleanReturnAsync();
    }
}
