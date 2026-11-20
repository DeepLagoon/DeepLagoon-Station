using System.Linq;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._NF.CryoSleep;
using Content.Server.Mind;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._NF.CCVar;
using Content.Shared.Inventory;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using static Content.Shared._NF.CryoSleep.SharedCryoSleepSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentCryosleepTests
{
    [Test]
    public async Task CryosleepWakesOutsideAnActiveApartmentAndRetainsTheStoredCharactersGear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        foreach (var session in sessions)
            await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(NFCCVars.CryoReturnEnabled, true);
            var apartments = entities.System<ApartmentSystem>();
            var inventory = entities.System<InventorySystem>();
            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(sessions[0].UserId);
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            var guest = entities.SpawnEntity("MobHuman", map.GridCoords);
            minds.TransferTo(mind.Owner, body);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], guest);
            var original = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            Assert.That(inventory.TryEquip(body, original, "jumpsuit", force: true), Is.True);
            foreach (var session in sessions)
                Assert.That(apartments.Enter(session, sessions[0].UserId, out var message), Is.True, message);
            var home = apartments.Instances[sessions[0].UserId];
            // A fallback-marked apartment pod must not select itself again when
            // the apartment remains awake because the other visitor is still there.
            var pod = entities.SpawnEntity("MachineCryoSleepPodFallback", new EntityCoordinates(home.Grid, 2.5f, 3.5f));
            var cryo = entities.System<CryoSleepSystem>();
            Assert.That(cryo.InsertBody(body, entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
            cryo.AcceptCryoSleep(body, pod);
            Assert.That(entities.GetComponent<MetaDataComponent>(home.Grid).EntityPaused, Is.False);
            Assert.That(apartments.TryGetVisit(sessions[0].UserId, out _, out _), Is.False);
            Assert.That(apartments.TryGetVisit(sessions[1].UserId, out _, out _), Is.True);
            var storedMap = entities.GetComponent<TransformComponent>(body).MapUid;
            Assert.That(cryo.TryReturnToBody(mind.Comp), Is.EqualTo(ReturnToBodyStatus.NoCryopodAvailable),
                "An apartment fallback cannot wake a character inside an ended visit.");
            Assert.That(cryo.HasCryosleepingBody(sessions[0].UserId), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(storedMap));

            var fallback = entities.SpawnEntity("MachineCryoSleepPodFallback", map.GridCoords);
            Assert.That(cryo.TryReturnToBody(mind.Comp), Is.EqualTo(ReturnToBodyStatus.Success));
            Assert.That(entities.GetComponent<CryoSleepComponent>(fallback).BodyContainer.ContainedEntity, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(map.Grid.Owner));
            Assert.That(apartments.TryGetVisit(sessions[0].UserId, out _, out _), Is.False);
            Assert.That(entities.HasComponent<ApartmentResidentComponent>(body), Is.False);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var returned), Is.True);
            Assert.That(returned, Is.EqualTo(original));
            Assert.That(cryo.HasCryosleepingBody(sessions[0].UserId), Is.False);
            Assert.That(cryo.EjectBody(fallback), Is.True);
            Assert.That(apartments.Enter(sessions[0], sessions[0].UserId, out var enter), Is.True, enter);
            Assert.That(apartments.Leave(sessions[0].UserId, out var leave), Is.True, leave);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out returned), Is.True);
            Assert.That(returned, Is.EqualTo(original));
            Assert.That(apartments.Leave(sessions[1].UserId, out leave), Is.True, leave);
            foreach (var session in sessions) pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(body); entities.DeleteEntity(guest);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CryosleepEndsVisitAndKeepsPersonalGearWithTheStoredCharacter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var session = await pair.Server.AddDummySession();
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            var apartments = entities.System<ApartmentSystem>();
            var inventory = entities.System<InventorySystem>();
            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(session.UserId);
            var body = entities.SpawnEntity("MobHuman", map.GridCoords);
            minds.TransferTo(mind.Owner, body);
            var original = entities.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            Assert.That(inventory.TryEquip(body, original, "jumpsuit", force: true), Is.True);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            var oldKit = entities.EntityQuery<ApartmentChameleonGearComponent>(true).Select(gear => gear.Owner).ToArray();
            var pod = entities.SpawnEntity("MachineCryoSleepPod", new EntityCoordinates(home.Grid, 2.5f, 3.5f));
            var cryo = entities.System<CryoSleepSystem>();
            Assert.That(cryo.InsertBody(body, entities.GetComponent<CryoSleepComponent>(pod), false), Is.True);
            cryo.AcceptCryoSleep(body, pod);
            Assert.That(cryo.HasCryosleepingBody(session.UserId), Is.True);
            var storedMap = entities.GetComponent<TransformComponent>(body).MapUid;
            Assert.That(storedMap, Is.Not.EqualTo(entities.GetComponent<TransformComponent>(home.Grid).MapUid));

            apartments.Update(0);

            Assert.That(entities.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(storedMap),
                "Apartment containment must not pull a successfully stored cryosleeper back into the apartment.");
            Assert.That(apartments.TryGetVisit(session.UserId, out _, out _), Is.False);
            Assert.That(entities.HasComponent<ApartmentResidentComponent>(body), Is.False);
            Assert.That(oldKit.All(item => !entities.EntityExists(item)), Is.True);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var returned), Is.True);
            Assert.That(returned, Is.EqualTo(original), "Escrow belongs to the stored character, not the next character on this account.");

            var replacement = entities.SpawnEntity("MobHuman", map.GridCoords);
            minds.TransferTo(mind.Owner, replacement);
            Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out returned), Is.True);
            Assert.That(returned, Is.EqualTo(original));
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            Assert.That(inventory.TryGetSlotEntity(replacement, "jumpsuit", out _), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            entities.DeleteEntity(replacement);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
}
