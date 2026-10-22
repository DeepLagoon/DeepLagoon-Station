using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Doors.Systems;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Inventory;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentElevatorTests
{
    [Test]
    public async Task KnownNumberNeedsNoInvitationAndRoundTripRestoresExactGear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var testMap = await pair.CreateTestMap();
        EntityUid body = default, uniform = default, source = default, destination = default;
        var doors = new List<EntityUid>();
        NetUserId owner = new(Guid.NewGuid());
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentStudio");
            var session = pair.Server.PlayerMan.Sessions.Single();
            var apartments = entities.System<ApartmentSystem>();
            var home = apartments.PrepareElevatorApartment(owner);
            var grid = testMap.Grid.Owner;
            var map = entities.System<SharedMapSystem>();
            for (var x = -2; x <= 3; x++)
            for (var y = -2; y <= 3; y++) map.SetTile(testMap.Grid, new Vector2i(x, y), testMap.Tile.Tile);
            var sourceCoordinates = new EntityCoordinates(grid, new Vector2(.5f, .5f));
            entities.EnsureComponent<ApartmentProtectedGridComponent>(grid);
            entities.SpawnEntity("DLApartmentBusStop", sourceCoordinates);
            source = entities.SpawnEntity("DLApartmentElevatorPanel", sourceCoordinates);
            destination = entities.SpawnEntity("DLApartmentElevatorPanel", new EntityCoordinates(home.Grid, new Vector2(2.5f, 2.5f)));
            foreach (var panel in new[] { source, destination })
            {
                var cabin = entities.GetComponent<ApartmentElevatorComponent>(panel);
                cabin.CabinMin = cabin.CabinMax = Vector2.Zero;
                cabin.DoorOffsets = new() { Vector2.UnitX };
                cabin.TravelTime = 4;
                var audio = entities.System<SharedAudioSystem>();
                Assert.That(audio.GetAudioLength(audio.ResolveSound(cabin.TravelSound!)).TotalSeconds, Is.EqualTo(12).Within(.0001));
                Assert.That(audio.GetAudioLength(audio.ResolveSound(cabin.ArrivalSound!)).TotalSeconds, Is.EqualTo(3).Within(.0001));
                var transform = entities.GetComponent<TransformComponent>(panel);
                var door = entities.SpawnEntity("Airlock", new EntityCoordinates(transform.GridUid!.Value, transform.LocalPosition + Vector2.UnitX));
                doors.Add(door);
                Assert.That(entities.GetComponent<TransformComponent>(door).Anchored, Is.True, "Door must stand on an existing floor tile.");
            }
            body = entities.SpawnEntity("MobHuman", sourceCoordinates);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", sourceCoordinates);
            Assert.That(entities.System<InventorySystem>().TryEquip(body, uniform, "jumpsuit", force: true), Is.True);
            var elevator = entities.System<Content.Server._DeepLagoon.Apartments.ApartmentElevatorSystem>();
            Assert.That(elevator.RequestRide(session, source, int.MaxValue, false), Does.Contain("Неизвестный номер"));
            Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.False);
            Assert.That(elevator.RequestRide(session, source, apartments.Number(owner), false), Does.Contain("Двери закрываются"));
        });
        // Moving out before the midpoint must leave inventory untouched.
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var origin = entities.GetComponent<TransformComponent>(source);
            entities.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(origin.GridUid!.Value, origin.LocalPosition + new Vector2(0, 2)));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.False);
            Assert.That(entities.System<ApartmentSystem>().TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(entities.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var equipped), Is.True);
            Assert.That(equipped, Is.EqualTo(uniform));
            var origin = entities.GetComponent<TransformComponent>(source);
            entities.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(origin.GridUid!.Value, origin.LocalPosition));
            Assert.That(entities.System<Content.Server._DeepLagoon.Apartments.ApartmentElevatorSystem>()
                .RequestRide(session, source, entities.System<ApartmentSystem>().Number(owner), false), Does.Contain("Двери закрываются"));
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            foreach (var door in doors)
            {
                Assert.That(pair.Server.EntMan.HasComponent<ApartmentElevatorDoorComponent>(door), Is.True);
                Assert.That(pair.Server.EntMan.GetComponent<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));
                Assert.That(pair.Server.EntMan.System<DoorSystem>().TryOpen(door), Is.False);
            }
        });
        await pair.RunTicksSync(90);
        await pair.Client.WaitAssertion(() =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var system = pair.Client.EntMan.System<Content.Client._DeepLagoon.Apartments.ApartmentElevatorSystem>();
            var curtain = system.GetType().GetField("_curtain", flags)!.GetValue(system)!;
            Assert.That((TimeSpan)curtain.GetType().GetField("_end", flags)!.GetValue(curtain)!, Is.EqualTo(TimeSpan.Zero));
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(entities.GetComponent<TransformComponent>(destination).GridUid));
            Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.True);
            foreach (var door in doors) Assert.That(entities.System<DoorSystem>().TryOpen(door), Is.False);
        });
        await pair.Client.WaitAssertion(() =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var system = pair.Client.EntMan.System<Content.Client._DeepLagoon.Apartments.ApartmentElevatorSystem>();
            var curtain = system.GetType().GetField("_curtain", flags)!.GetValue(system)!;
            var start = (TimeSpan)curtain.GetType().GetField("_start", flags)!.GetValue(curtain)!;
            var end = (TimeSpan)curtain.GetType().GetField("_end", flags)!.GetValue(curtain)!;
            Assert.That((end - start).TotalSeconds, Is.EqualTo(2).Within(.001));
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.HasComponent<ApartmentTripPendingComponent>(body), Is.True);
            foreach (var door in doors) Assert.That(pair.Server.EntMan.HasComponent<ApartmentElevatorDoorComponent>(door), Is.True);
        });
        await pair.RunTicksSync(120);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var apartments = entities.System<ApartmentSystem>();
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(apartments.TryGetVisit(session.UserId, out var home, out var resident), Is.True);
            Assert.That(home.Owner, Is.EqualTo(owner));
            Assert.That(resident, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(home.Grid));
            Assert.That(entities.HasComponent<ApartmentTripPendingComponent>(body), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(uniform).MapID, Is.Not.EqualTo(entities.GetComponent<TransformComponent>(body).MapID));
            Assert.That(entities.System<Content.Server._DeepLagoon.Apartments.ApartmentElevatorSystem>()
                .RequestRide(session, destination, 0, true), Does.Contain("Двери закрываются"));
        });
        await pair.RunTicksSync(420);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(entities.System<ApartmentSystem>().TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(entities.GetComponent<TransformComponent>(source).GridUid));
            Assert.That(entities.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(uniform));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
}
