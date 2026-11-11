using System;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._DeepLagoon.Power;
using Content.Server.Power.Components;
using Content.Server.Research.Components;
using Content.Shared._Goobstation.ItemMiner;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PoiProductionTests
{
    private static readonly EntProtoId PlayerDataFarm = "DatafarmResearch";

    [TestCase("derelictdrillsite", "StationLaserDrill", 1)]
    [TestCase("sevastopol", "DatafarmResearchSevastopol", 7)]
    public async Task PoiKeepsOriginalPowerAndReducesProduction(string file, string machinePrototype, int nativeCount)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGridTracked(mapId, new ResPath($"/Maps/_Mono/POI/{file}.yml"), out var loaded), Is.True);
            var grid = loaded!.Value.Owner;
            var restriction = entities.GetComponent<PoiPowerRestrictionComponent>(grid);
            Assert.That(restriction.NativeSources, Has.Count.EqualTo(nativeCount));

            foreach (var source in restriction.NativeSources)
            {
                Assert.That(entities.GetComponent<MetaDataComponent>(source).EntityPrototype?.ID, Is.EqualTo("GeneratorRTG"));
                Assert.That(entities.GetComponent<TransformComponent>(source).GridUid, Is.EqualTo(grid));
                Assert.That(entities.GetComponent<PowerSupplierComponent>(source).Enabled, Is.True);
            }

            var machines = 0;
            var query = entities.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var metadata, out var transform))
            {
                if (transform.GridUid != grid || metadata.EntityPrototype?.ID != machinePrototype)
                    continue;

                machines++;
                if (file == "derelictdrillsite")
                    Assert.That(entities.GetComponent<ItemMinerComponent>(uid).Interval, Is.EqualTo(TimeSpan.FromSeconds(11.5)));
                else
                    Assert.That(entities.GetComponent<ResearchPointSourceComponent>(uid).PointsPerSecond, Is.EqualTo(20));
            }

            Assert.That(machines, Is.EqualTo(2));
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var farm = prototypes.Index(PlayerDataFarm);
            Assert.That(farm.TryComp<ResearchPointSourceComponent>(out var pointSource, entities.ComponentFactory), Is.True);
            Assert.That(pointSource!.PointsPerSecond, Is.EqualTo(2));

            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
