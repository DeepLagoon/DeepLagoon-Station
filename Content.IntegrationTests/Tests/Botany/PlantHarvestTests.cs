using System.Linq;
using Content.Client.Botany;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Botany.Components;
using Content.Shared.Botany.Events;
using Content.Shared.Botany.Items.Components;
using Content.Shared.Botany.Systems;
using Content.Shared.Interaction;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Client.GameObjects;

namespace Content.IntegrationTests.Tests.Botany;

[TestFixture]
public sealed class PlantHarvestTests
{
    [Test]
    public async Task HarvestSpawnsProduceAndStartsNextProductionCycle()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var tray = entities.SpawnEntity("hydroponicsTray", map.GridCoords);
            var plant = entities.SpawnEntity("TomatoPlants", map.GridCoords);
            var user = entities.SpawnEntity(null, map.GridCoords);
            entities.System<PlantTraySystem>().PlantingPlantInTray(tray, plant);
            var holder = entities.GetComponent<PlantHolderComponent>(plant);
            var data = entities.GetComponent<PlantComponent>(plant);
            var harvest = entities.System<PlantHarvestSystem>();
            holder.Age = 20;
            holder.ReadyForHarvest = true;

            var interact = new InteractHandEvent(user, plant);
            entities.EventBus.RaiseLocalEvent(plant, interact);

            Assert.That(interact.Handled, Is.True);
            Assert.That(holder.ReadyForHarvest, Is.False);
            Assert.That(holder.LastHarvest, Is.EqualTo(holder.Age));
            var produce = entities.EntityQuery<ProduceComponent>().ToArray();
            Assert.That(produce, Has.Length.EqualTo(data.Yield));
            foreach (var item in produce)
            {
                var prototype = item.PlantProtoId;
                Assert.That(prototype.ToString(), Is.EqualTo("TomatoPlants"));
                Assert.That(item.PlantData, Is.Not.Null);
                Assert.That(entities.GetComponent<PlantComponent>(item.PlantData!.Value).Potency, Is.EqualTo(data.Potency));
                Assert.That(entities.System<SharedSolutionContainerSystem>().TryGetSolution(item.Owner, item.TargetSolution, out _, out var solution), Is.True);
                Assert.That(solution!.Volume.Float(), Is.GreaterThan(0));
            }

            Assert.That(harvest.TryHandleHarvest(plant, user), Is.False);
            Assert.That(entities.EntityQuery<ProduceComponent>().Count(), Is.EqualTo(data.Yield));

            holder.Age += (int) data.Production + 1;
            var grow = new PlantGrowEvent(tray);
            entities.EventBus.RaiseLocalEvent(plant, ref grow);
            Assert.That(holder.ReadyForHarvest, Is.True);
            Assert.That(harvest.TryHandleHarvest(plant, user), Is.True);
            Assert.That(holder.ReadyForHarvest, Is.False);
            Assert.That(entities.EntityQuery<ProduceComponent>().Count(), Is.EqualTo(data.Yield * 2));
        });
        await pair.CleanReturnAsync();
    }
}

[TestFixture]
public sealed class PlantHarvestInteractionTests : InteractionTest
{
    [Test]
    public async Task ClientClickHarvestsPlantAndUpdatesItsState()
    {
        await SpawnTarget("TomatoPlants");
        await Server.WaitPost(() =>
        {
            var plant = STarget!.Value;
            var tray = SEntMan.SpawnEntity("hydroponicsTray", SEntMan.GetCoordinates(TargetCoords));
            SEntMan.System<PlantTraySystem>().PlantingPlantInTray(tray, plant);
            var holder = SEntMan.GetComponent<PlantHolderComponent>(plant);
            holder.Age = 20;
            holder.ReadyForHarvest = true;
            holder.NextUpdate = TimeSpan.MaxValue;
            SEntMan.Dirty(plant, holder);
        });
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            Assert.That(sprite[sprite.LayerMapGet(PlantLayers.Plant)].RsiState.Name, Is.EqualTo("harvest"));
        });
        await PressKey(EngineKeyFunctions.Use);
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<PlantHolderComponent>(STarget!.Value).ReadyForHarvest, Is.False);
            Assert.That(SEntMan.EntityQuery<ProduceComponent>().Count(), Is.EqualTo(2));
        });
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<PlantHolderComponent>(CTarget!.Value).ReadyForHarvest, Is.False);
            Assert.That(CEntMan.EntityQuery<ProduceComponent>().Count(), Is.EqualTo(2));
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget.Value);
            Assert.That(sprite[sprite.LayerMapGet(PlantLayers.Plant)].RsiState.Name, Is.EqualTo("stage-6"));
        });
    }
}
