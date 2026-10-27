using Content.Shared.Botany.Components;
using Content.Shared.Botany.Events;
using Content.Shared.Botany.Systems;

namespace Content.IntegrationTests.Tests.Botany;

[TestFixture]
public sealed class PlantToxinsTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: PlantToxinRegressionTray
  components:
  - type: PlantTray
- type: entity
  id: PlantToxinRegressionPlant
  components:
  - type: Plant
    lifespan: 1000
  - type: PlantHolder
  - type: PlantToxins
";

    [TestCase(20f, 98f, 18f)]
    [TestCase(2f, 100f, 1f)]
    public async Task GrowthConsumesToxinsAndDamagesOnlyAboveTolerance(float toxins, float health, float remaining)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var tray = entities.SpawnEntity("PlantToxinRegressionTray", map.GridCoords);
            var plant = entities.SpawnEntity("PlantToxinRegressionPlant", map.GridCoords);
            entities.System<PlantTraySystem>().AdjustToxin(tray, toxins);
            var grow = new PlantGrowEvent(tray);
            entities.EventBus.RaiseLocalEvent(plant, ref grow);
            Assert.That(entities.GetComponent<PlantHolderComponent>(plant).Health, Is.EqualTo(health));
            Assert.That(entities.GetComponent<PlantTrayComponent>(tray).ToxinLevel, Is.EqualTo(remaining));
        });
        await pair.CleanReturnAsync();
    }
}
