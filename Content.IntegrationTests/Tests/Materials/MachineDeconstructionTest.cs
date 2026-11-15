#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Server.Construction;
using Content.Server.Materials;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Fluids.Components;
using Content.Shared.Materials;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Materials;

[TestFixture]
public sealed class MachineDeconstructionTest
{
    [Test]
    public async Task MachineDeconstructionPreservesStoredMaterialsItemsAndReagents()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var map = await pair.CreateTestMap();
        var construction = server.System<ConstructionSystem>();
        var materials = server.System<MaterialStorageSystem>();
        var containers = server.System<SharedContainerSystem>();
        var transform = server.System<SharedTransformSystem>();
        var solutions = server.System<SharedSolutionContainerSystem>();
        var tags = server.System<TagSystem>();

        var dismantled = new List<EntityUid>();
        var storedItems = new List<EntityUid>();

        EntityUid Spawn(string prototype) => entities.SpawnEntity(prototype, map.GridCoords);

        void Dismantle(EntityUid machine)
        {
            var node = construction.GetCurrentNode(machine);
            Assert.That(node, Is.Not.Null);
            var edge = node!.Edges.Single(edge => edge.Target == "machineFrame");
            transform.Unanchor(machine);
            construction.PerformActions(machine, null, edge.Completed);
            Assert.That(construction.ChangeNode(machine, null, edge.Target), Is.True);
            dismantled.Add(machine);
        }

        await server.WaitAssertion(() =>
        {
            var processor = Spawn("ScrapProcessor");
            Assert.That(materials.TryChangeMaterialAmount(processor, "RawScrap", 94, localOnly: true), Is.True);
            Dismantle(processor);

            var silo = Spawn("MachineMaterialSilo");
            Assert.That(materials.TryChangeMaterialAmount(silo, "Gold", 365, localOnly: true), Is.True);
            Dismantle(silo);

            var cabinets = new[]
            {
                ("KitchenReagentGrinder", "inputContainer", "FoodApple"),
                ("SmartFridge", "smart_fridge_inventory", "FoodApple"),
                ("TelecomServer", "key_slots", "EncryptionKeyCommon"),
                ("MachineOreMagnet", "storagebase", "ScrapOre1"),
            };
            foreach (var (machine, container, item) in cabinets)
            {
                var machineId = Spawn(machine);
                var itemId = Spawn(item);
                Assert.That(containers.Insert(itemId, containers.GetContainer(machineId, container)), Is.True);
                storedItems.Add(itemId);
                Dismantle(machineId);
            }

            var reclaimer = Spawn("MaterialReclaimer");
            entities.AddComponent<ActiveMaterialReclaimerComponent>(reclaimer);
            var reclaiming = Spawn("DrinkGlass");
            Assert.That(containers.Insert(reclaiming, containers.GetContainer(reclaimer, SharedMaterialReclaimerSystem.ActiveReclaimerContainerId)), Is.True);
            storedItems.Add(reclaiming);
            Dismantle(reclaimer);

            var chemMaster = Spawn("ChemMaster");
            Assert.That(solutions.TryGetSolution(chemMaster, "buffer", out _, out var buffer), Is.True);
            buffer!.AddReagent("Water", 10);
            Dismantle(chemMaster);

            var puddles = entities.EntityQueryEnumerator<PuddleComponent, TransformComponent>();
            Assert.That(puddles.MoveNext(out var puddle, out _, out var puddleTransform), Is.True);
            Assert.That(puddleTransform!.MapID, Is.EqualTo(map.MapId));
            Assert.That(solutions.TryGetSolution(puddle, "puddle", out _, out var liquid), Is.True);
            Assert.That(liquid!.Volume.Float(), Is.EqualTo(10));
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            foreach (var machine in dismantled)
                Assert.That(entities.EntityExists(machine), Is.False);

            foreach (var item in storedItems)
            {
                Assert.That(entities.EntityExists(item), Is.True);
                Assert.That(entities.GetComponent<TransformComponent>(item).ParentUid, Is.EqualTo(map.Grid.Owner));
            }

            var gold = 0;
            var scrap = 0;
            var query = entities.EntityQueryEnumerator<PhysicalCompositionComponent, MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var composition, out var metadata, out var xform))
            {
                if (xform.MapID != map.MapId)
                    continue;

                var count = entities.TryGetComponent<StackComponent>(uid, out var stack) ? stack.Count : 1;
                if (composition.MaterialComposition.TryGetValue("Gold", out var goldAmount) &&
                    metadata.EntityPrototype?.ID is "IngotGold1" or "MaterialResidue")
                    gold += goldAmount * count;

                if (metadata.EntityPrototype?.ID != "MaterialResidue" || !composition.MaterialComposition.TryGetValue("RawScrap", out var scrapAmount))
                    continue;

                Assert.That(tags.HasTag(uid, "Ore"), Is.True);
                Assert.That(stack, Is.Null);
                scrap += scrapAmount;
            }

            Assert.That(gold, Is.EqualTo(365));
            Assert.That(scrap, Is.EqualTo(94));
        });

        await pair.CleanReturnAsync();
    }
}
