using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Server._DeepLagoon.LustWish;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LustWishTests
{
    [Test]
    public async Task CatalogSpawnsAndMetabolismRespectsDoseAndRecovery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var pack = prototypes.Index<VendingMachineInventoryPrototype>("DLLustWishInventory");
            foreach (var id in pack.StartingInventory.Keys.Concat(pack.ContrabandInventory!.Keys).Concat(pack.EmaggedInventory!.Keys))
            {
                var item = entities.SpawnEntity(id, MapCoordinates.Nullspace);
                entities.DeleteEntity(item);
            }
            var body = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var arousal = entities.GetComponent<InteractionPanelArousalComponent>(body);
            var system = entities.System<InteractionPanelArousalSystem>();
            var crocin = prototypes.Index<ReagentPrototype>("DLCrocin");
            var effect = (AdjustArousal) crocin.Metabolisms!.Single().Value.Effects.Single();
            effect.Effect(new EntityEffectReagentArgs(body, entities, null, null, FixedPoint2.New(0.5f), crocin, null, 1));
            Assert.That(arousal.Current, Is.EqualTo(3).Within(0.001));
            var camphor = prototypes.Index<ReagentPrototype>("DLCamphor");
            var calming = (AdjustArousal) camphor.Metabolisms!.Single().Value.Effects.Single();
            calming.Effect(new EntityEffectReagentArgs(body, entities, null, null, FixedPoint2.New(0.25f), camphor, null, 1));
            Assert.That(arousal.Current, Is.Zero);
            system.AdjustArousal(body, body, arousal.Maximum);
            Assert.That(arousal.Current, Is.Zero);
            system.AdjustArousal(body, body, 20);
            Assert.That(arousal.Current, Is.Zero, "Recovery must reject positive stimulation");
            Assert.That(effect.GuidebookEffectDescription(prototypes, entities.EntitySysManager), Is.Not.Empty);
            entities.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CardTogglesPaymentAndPortalDisconnectRevokesRemoteReach()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var coordinates = new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f));
            var distant = new EntityCoordinates(map.Grid, new Vector2(20.5f, 0.5f));
            var target = entities.SpawnEntity("MobHuman", coordinates);
            var user = entities.SpawnEntity("MobHuman", distant);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], target);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], user);
            var hands = entities.System<SharedHandsSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var mapSystem = entities.System<SharedMapSystem>();
            for (var x=0; x<=31; x++)
                for (var y=0; y<=2; y++)
                    mapSystem.SetTile(map.Grid.Owner,map.Grid.Comp,new EntityCoordinates(map.Grid,new Vector2(x,y)),map.Tile.Tile);
            var wallA = entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, new Vector2(.5f, 1.5f)));
            var wallB = entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid, new Vector2(20.5f, 1.5f)));
            var kit = entities.SpawnEntity("DLLustWishPortalKit", coordinates);
            Assert.That(hands.TryPickupAnyHand(target, kit), Is.True);
            entities.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(target, kit, wallA, coordinates, true));
            var kitComponent = entities.GetComponent<LustWishPortalKitComponent>(kit);
            Assert.That(kitComponent.FirstPortal, Is.Not.Null);
            var first = kitComponent.FirstPortal.Value;
            transforms.SetCoordinates(target, distant);
            entities.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(target, kit, wallB, distant, true));
            Assert.That(kitComponent.Completed, Is.True);
            var second = entities.GetComponent<LustWishPortalComponent>(first).Peer!.Value;
            Assert.That(entities.GetComponent<LustWishPortalComponent>(second).Peer, Is.EqualTo(first));
            entities.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(target, kit, wallA, coordinates, true));
            Assert.That(entities.GetComponent<LustWishPortalComponent>(first).Peer, Is.EqualTo(second), "A spent kit cannot create a third endpoint");
            transforms.SetCoordinates(target, coordinates);
            var portals = entities.System<LustWishPortalSystem>();
            Assert.That(portals.CanRelay(user, target), Is.False);
            Assert.That(portals.TryConnect(first, target), Is.True);
            Assert.That(portals.CanRelay(user, target), Is.True);
            portals.Disconnect(target);
            Assert.That(portals.CanRelay(user, target), Is.False);
            Assert.That(portals.TryConnect(first, target), Is.True);
            portals.Disconnect(target);
            transforms.SetCoordinates(target, distant);
            Assert.That(portals.CanRelay(user, target), Is.False, "Unbuckling revokes remote access immediately");
            transforms.SetCoordinates(target, coordinates);
            Assert.That(portals.TryConnect(first, target), Is.True);
            entities.DeleteEntity(first);
            Assert.That(entities.GetComponent<LustWishPortalComponent>(second).Peer, Is.Null);
            Assert.That(portals.CanRelay(user, target), Is.False);
            var vendor = entities.SpawnEntity("VendingMachineLustWish", distant);
            var card = entities.SpawnEntity("DLLustWishEliteCard", distant);
            Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(user, card), Is.True);
            var machine = entities.GetComponent<VendingMachineComponent>(vendor);
            Assert.That(machine.RequiresCash, Is.True);
            for (var i = 0; i < 2; i++)
            {
                var use = new InteractUsingEvent(user, card, vendor, distant);
                entities.EventBus.RaiseLocalEvent(vendor, use);
                Assert.That(use.Handled, Is.True);
                Assert.That(machine.RequiresCash, Is.EqualTo(i == 1));
            }
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], null);
            foreach (var uid in new[] { target, user, vendor, card, second }) entities.DeleteEntity(uid);
        });
        // Drain map/player events before closing the dummy sessions.
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }
}
