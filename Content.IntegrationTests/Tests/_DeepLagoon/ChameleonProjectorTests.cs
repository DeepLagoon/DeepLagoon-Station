using System.Linq;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Polymorph.Components;
using Content.Shared.Stealth.Components;
using Robust.Client.GameObjects;
using Robust.Client.Timing;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using ClientProjector = Content.Client.Polymorph.Systems.ChameleonProjectorSystem;
using ServerProjector = Content.Server.Polymorph.Systems.ChameleonProjectorSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ChameleonProjectorTests
{
    [Test]
    public async Task RepeatedDisguisesAndRevealRestoreClientVisibility()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default, projector = default, source = default;
        NetEntity netBody = default, netDisguise = default, netSource = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            body = e.SpawnEntity("MobHuman", map.GridCoords);
            projector = e.SpawnEntity("ChameleonProjector", map.GridCoords);
            source = e.SpawnEntity("DrinkGlass", map.GridCoords);
            netSource = e.GetNetEntity(source);
            netBody = e.GetNetEntity(body);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
        });
        await pair.RunTicksSync(10);

        for (var i = 0; i < 3; i++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                var e = pair.Server.EntMan;
                Assert.That(e.System<ServerProjector>().TryDisguise(
                    (projector, e.GetComponent<ChameleonProjectorComponent>(projector)), body, source), Is.True);
                netDisguise = e.GetNetEntity(e.GetComponent<ChameleonDisguisedComponent>(body).Disguise);
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var e = pair.Client.EntMan;
                Assert.That(e.GetComponent<SpriteComponent>(e.GetEntity(netBody)).Visible, Is.False);
                var disguise = e.GetComponent<SpriteComponent>(e.GetEntity(netDisguise));
                var original = e.GetComponent<SpriteComponent>(e.GetEntity(netSource));
                Assert.That(disguise.Visible, Is.True);
                Assert.That(disguise.BaseRSI, Is.SameAs(original.BaseRSI));
                Assert.That(disguise.AllLayers.Select(layer => layer.RsiState),
                    Is.EqualTo(original.AllLayers.Select(layer => layer.RsiState)));
                Assert.That(disguise.AllLayers.Any(layer => layer.Visible), Is.True,
                    "The glass disguise must have a visible sprite layer.");
            });
        }

        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.System<ServerProjector>().TryReveal(body), Is.True));
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var clientBody = e.GetEntity(netBody);
            Assert.That(e.HasComponent<ChameleonDisguisedComponent>(clientBody), Is.False);
            Assert.That(e.GetComponent<SpriteComponent>(clientBody).Visible, Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ContainerStateDoesNotDeleteAuthoritativeDisguiseOnClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        NetEntity netBody = default, netDisguise = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            var projector = e.SpawnEntity("ChameleonProjector", map.GridCoords);
            var source = e.SpawnEntity("DrinkGlass", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
            Assert.That(e.System<ServerProjector>().TryDisguise(
                (projector, e.GetComponent<ChameleonProjectorComponent>(projector)), body, source), Is.True);
            netBody = e.GetNetEntity(body);
            netDisguise = e.GetNetEntity(e.GetComponent<ChameleonDisguisedComponent>(body).Disguise);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var body = e.GetEntity(netBody);
            var disguise = e.GetEntity(netDisguise);
            var container = e.System<SharedContainerSystem>().EnsureContainer<Container>(
                e.SpawnEntity(null, e.GetComponent<TransformComponent>(body).Coordinates), "test-container");
            // Container state can arrive before the server's disguise removal.
            using (pair.Client.ResolveDependency<IClientGameTiming>().StartStateApplicationArea())
            {
                e.EventBus.RaiseLocalEvent(body, new EntGotInsertedIntoContainerMessage(body, container));
                Assert.That(e.EntityExists(disguise), Is.True,
                    "Client container events must wait for the authoritative reveal.");
                Assert.That(e.HasComponent<ChameleonDisguisedComponent>(body), Is.True);
            }
            Assert.That(e.System<ClientProjector>().TryReveal(body), Is.False,
                "Revealing deletes network entities and cannot be predicted.");
        });
        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.System<ServerProjector>().TryReveal(
                pair.Server.EntMan.GetEntity(netBody)), Is.True));
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
            Assert.That(pair.Client.EntMan.GetComponent<SpriteComponent>(
                pair.Client.EntMan.GetEntity(netBody)).Visible, Is.True));
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ClientClothingEventsDoNotCreateOrRemoveStealth(bool applyingState)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        NetEntity netBody = default, netHelmet = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            var helmet = e.SpawnEntity("ClothingHeadHelmetHardsuitCybersunStealth", map.GridCoords);
            netBody = e.GetNetEntity(body);
            netHelmet = e.GetNetEntity(helmet);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var body = e.GetEntity(netBody);
            var helmet = e.GetEntity(netHelmet);
            var inventory = e.System<InventorySystem>();
            using StateApplicationGuard? state = applyingState
                ? pair.Client.ResolveDependency<IClientGameTiming>().StartStateApplicationArea()
                : null;

            Assert.That(inventory.TryEquip(body, helmet, "head", force: true), Is.True);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.False,
                "The server replicates granted components; client equip events must not invent them.");
            Assert.That(e.HasComponent<StealthOnMoveComponent>(body), Is.False);
            // These represent components received from the server, independent of local equip bookkeeping.
            var stealth = e.AddComponent<StealthComponent>(body);
            var movement = e.AddComponent<StealthOnMoveComponent>(body);
            Assert.That(inventory.TryUnequip(body, "head", force: true), Is.True);
            Assert.That(e.GetComponent<StealthComponent>(body), Is.SameAs(stealth));
            Assert.That(e.GetComponent<StealthOnMoveComponent>(body), Is.SameAs(movement));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PredictedHelmetRemovalRollsBackWithoutChangingStealth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        NetEntity netBody = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            var helmet = e.SpawnEntity("ClothingHeadHelmetHardsuitCybersunStealth", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(body, helmet, "head", force: true), Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.True);
            Assert.That(e.HasComponent<StealthOnMoveComponent>(body), Is.True);
            netBody = e.GetNetEntity(body);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var body = e.GetEntity(netBody);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.True);
            Assert.That(e.System<InventorySystem>().TryGetSlotEntity(body, "head", out var helmet), Is.True);
            // Reproduce equip-before-component-state ordering: the old client recorded these
            // as its own grants and removed them on predicted unequip. Rollback then re-added
            // them from container events while enumerating the wearer's network components.
            var grants = e.GetComponent<ClothingGrantComponentComponent>(helmet.Value);
            grants.Active["Stealth"] = true;
            grants.Active["StealthOnMove"] = true;
            Assert.That(e.System<InventorySystem>().TryUnequip(body, "head", force: true, predicted: true), Is.True);
        });
        // The server never acknowledged the local removal. Restore its inventory state.
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var body = e.GetEntity(netBody);
            Assert.That(e.System<InventorySystem>().TryGetSlotEntity(body, "head", out _), Is.True);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.True);
            Assert.That(e.HasComponent<StealthOnMoveComponent>(body), Is.True);
        });
        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.System<InventorySystem>().TryUnequip(
                pair.Server.EntMan.GetEntity(netBody), "head", force: true), Is.True));
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var body = e.GetEntity(netBody);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.False);
            Assert.That(e.HasComponent<StealthOnMoveComponent>(body), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RepeatedUnequipDoesNotRemoveComponentsTheClothingNoLongerGrants()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            var helmet = e.SpawnEntity("ClothingHeadHelmetHardsuitCybersunStealth", map.GridCoords);
            var inventory = e.System<InventorySystem>();
            Assert.That(inventory.TryEquip(body, helmet, "head", force: true), Is.True);
            Assert.That(inventory.TryUnequip(body, "head", force: true), Is.True);
            Assert.That(e.HasComponent<StealthComponent>(body), Is.False);
            var stealth = e.AddComponent<StealthComponent>(body);
            var movement = e.AddComponent<StealthOnMoveComponent>(body);
            Assert.That(inventory.TryGetSlot(body, "head", out var slot), Is.True);
            e.EventBus.RaiseLocalEvent(helmet, new GotUnequippedEvent(body, helmet, slot));
            Assert.That(e.GetComponent<StealthComponent>(body), Is.SameAs(stealth));
            Assert.That(e.GetComponent<StealthOnMoveComponent>(body), Is.SameAs(movement));
        });
        await pair.CleanReturnAsync();
    }
}
