using System.Linq;
using System.Numerics;
using Content.Shared.APC;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParallelInterfaceRangeTests
{
    [Test]
    public async Task HandActivationPrefersPdaUiAndStillEnablesPersonalAi()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            var pda = entities.SpawnEntity("PassengerPDA", map.GridCoords);
            var hands = entities.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
            Assert.That(hands.TryPickupAnyHand(actor, pda, checkActionBlocker: false), Is.True);
            var interaction = entities.System<SharedInteractionSystem>();
            var ui = entities.System<SharedUserInterfaceSystem>();
            Assert.That(interaction.UseInHandInteraction(actor, pda), Is.True);
            Assert.That(ui.IsUiOpen(pda, PdaUiKey.Key, actor), Is.True);
            Assert.That(interaction.UseInHandInteraction(actor, pda, checkUseDelay: false), Is.True);
            Assert.That(ui.IsUiOpen(pda, PdaUiKey.Key, actor), Is.False, "Using the PDA again still closes its interface.");
            Assert.That(entities.HasComponent<Content.Server.Ghost.Roles.Components.GhostTakeoverAvailableComponent>(pda), Is.False);

            var pai = entities.SpawnEntity("PersonalAI", map.GridCoords);
            Assert.That(interaction.InteractionActivate(actor, pai), Is.True);
            Assert.That(entities.HasComponent<Content.Server.Ghost.Roles.Components.GhostTakeoverAvailableComponent>(pai), Is.True,
                "A device without an activatable UI must still enable its ghost role.");
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("belt")]
    [TestCase("id")]
    public async Task InventoryPdaActivationStaysOpen(string slot)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid actor = default, pda = default;
        NetEntity netActor = default, netPda = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            pda = entities.SpawnEntity("PassengerPDA", map.GridCoords);
            Assert.That(entities.System<InventorySystem>().TryEquip(actor, pda, slot, silent: true, force: true), Is.True);
            netActor = entities.GetNetEntity(actor);
            netPda = entities.GetNetEntity(pda);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            Assert.That(entities.System<InventorySystem>().TryGetSlotEntity(entities.GetEntity(netActor), slot, out var item), Is.True);
            Assert.That(item, Is.EqualTo(entities.GetEntity(netPda)));
            entities.System<Content.Client.Inventory.ClientInventorySystem>().UIInventoryActivateItem(slot, entities.GetEntity(netActor));
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.System<SharedUserInterfaceSystem>().IsUiOpen(pda, PdaUiKey.Key, actor), Is.True,
                "Activating an equipped PDA from its inventory slot must leave its UI open on the server.");
            Assert.That(entities.HasComponent<Content.Server.Ghost.Roles.Components.GhostTakeoverAvailableComponent>(pda), Is.False,
                "Opening a PDA must not enable a ghost role instead of its interface.");
        });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            Assert.That(entities.System<SharedUserInterfaceSystem>().IsUiOpen(entities.GetEntity(netPda), PdaUiKey.Key, entities.GetEntity(netActor)), Is.True);
        });
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MultipleMountedInterfacesAndEquippedPdaSurviveParallelRangeChecks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid actor = default, pda = default;
        EntityUid[] devices = new EntityUid[4];
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(map.Grid.Owner, map.Grid.Comp, Enumerable.Range(-2, 5)
                .SelectMany(x => Enumerable.Range(-2, 5).Select(y => (new Vector2i(x, y), floor))).ToList());
            var center = new Vector2(.5f, .5f);
            entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid.Owner, center));
            actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, center + Angle.Zero.ToWorldVec()));
            for (var i = 0; i < devices.Length; i++)
                devices[i] = entities.SpawnEntity("APCBasic", new EntityCoordinates(map.Grid.Owner, center));
            pda = entities.SpawnEntity("PassengerPDA", new EntityCoordinates(map.Grid.Owner, center));
            Assert.That(entities.System<InventorySystem>().TryEquip(actor, pda, "id", silent: true, force: true), Is.True);
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var ui = entities.System<SharedUserInterfaceSystem>();
            foreach (var device in devices)
            {
                Assert.That(entities.System<SharedInteractionSystem>().InRangeAndAccessible(actor, device), Is.True);
                ui.OpenUi(device, ApcUiKey.Key, actor);
            }
            ui.OpenUi(pda, PdaUiKey.Key, actor);
            Assert.That(ui.IsUiOpen(pda, PdaUiKey.Key, actor), Is.True);
        });
        // More than two active interfaces engages the engine's worker threads.
        await pair.RunTicksSync(10);
        await pair.Server.WaitAssertion(() =>
        {
            var ui = pair.Server.EntMan.System<SharedUserInterfaceSystem>();
            foreach (var device in devices)
                Assert.That(ui.IsUiOpen(device, ApcUiKey.Key, actor), Is.True, "Mounted UI must survive checks on a worker thread.");
            Assert.That(ui.IsUiOpen(pda, PdaUiKey.Key, actor), Is.True, "An equipped PDA must remain usable alongside other open interfaces.");
            pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(actor,
                new EntityCoordinates(map.Grid.Owner, new Vector2(6.5f, .5f)));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var ui = pair.Server.EntMan.System<SharedUserInterfaceSystem>();
            foreach (var device in devices)
                Assert.That(ui.IsUiOpen(device, ApcUiKey.Key, actor), Is.False, "Distance checks must still close inaccessible UIs.");
            Assert.That(ui.IsUiOpen(pda, PdaUiKey.Key, actor), Is.True, "The equipped PDA moves with its owner.");
        });
        await pair.CleanReturnAsync();
    }
}
