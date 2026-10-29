using System;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class FlammableCollectionTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: FlammableCollectionFuel
  components:
  - type: Appearance
  - type: Flammable
    damage:
      types:
        Heat: 1
  - type: Damageable
  - type: Destructible
    thresholds:
    - trigger:
        !type:DamageTrigger
        damage: 1
      behaviors:
      - !type:SpawnEntitiesBehavior
        spawn:
          FlammableCollectionProduct:
            min: 1
            max: 1
- type: entity
  id: FlammableCollectionProduct
  components:
  - type: Appearance
  - type: Flammable
";

    [Test]
    public async Task FireDamageCanSpawnAnotherFlammableDuringUpdate()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var atmos = entities.System<AtmosphereSystem>();
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 100;
            atmos.SetMapAtmosphere(map, false, new GasMixture(moles, Atmospherics.T20C));
            var fuel = entities.SpawnEntity("FlammableCollectionFuel", new MapCoordinates(0, 0, mapId));
            var other = entities.SpawnEntity("FlammableCollectionProduct", new MapCoordinates(2, 0, mapId));
            var fire = entities.System<FlammableSystem>();
            var component = entities.GetComponent<FlammableComponent>(fuel);
            fire.SetFireStacks(fuel, 1, component, ignite: true);
            component.NextUpdate = TimeSpan.Zero;
            entities.GetComponent<FlammableComponent>(other).NextUpdate = TimeSpan.Zero;
            Assert.That(atmos.GetContainingMixture(fuel)?.GetMoles(Gas.Oxygen), Is.GreaterThan(2));
            Assert.DoesNotThrow(() => fire.Update(0));
            var products = 0;
            var query = entities.EntityQueryEnumerator<FlammableComponent, MetaDataComponent>();
            while (query.MoveNext(out _, out _, out var metadata))
                if (metadata.EntityPrototype?.ID == "FlammableCollectionProduct") products++;
            Assert.That(products, Is.EqualTo(2), "The heat-damage callback must really spawn another flammable entity");
            Assert.That(entities.GetComponent<FlammableComponent>(other).NextUpdate, Is.GreaterThan(TimeSpan.Zero));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
