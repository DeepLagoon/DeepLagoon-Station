using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Mapping;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using PointLightComponent = Robust.Server.GameObjects.PointLightComponent;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunLightingTests
{
    [Test]
    public async Task SourceAmbientReachesClientAndReplaceableBulbsControlActualLocalLight()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        EntityUid cold = default, broken = default;
        await pair.Server.WaitAssertion(() =>
        {
            var source = server.AddComponent<BitrunAreaLightingComponent>(map.Grid);
            source.Tiles = new Dictionary<Vector2i, Color> { [new(0, 0)] = Color.White, [new(1, 0)] = Color.FromHex("#8589FA") };
            server.Dirty(map.Grid, source);
            var origin = map.GridCoords.Offset(new Vector2(.5f, .5f));
            var observer = server.SpawnEntity("MobObserver", origin);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
            var environment = server.EnsureComponent<GridEnvironmentComponent>(map.Grid);
            environment.AlwaysPowered = true;
            cold = server.SpawnEntity("BitrunMapMachineryLightColdDirectionalNorthVariantBase", origin);
            broken = server.SpawnEntity("BitrunMapMachineryLightBroken", origin.Offset(new Vector2(1, 0)));
            server.System<GridEnvironmentSystem>().RefreshGrid(map.Grid, environment);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var lights = server.System<SharedPoweredLightSystem>();
            var bulb = lights.GetBulb(cold);
            Assert.That(bulb, Is.Not.Null);
            var data = server.GetComponent<LightBulbComponent>(bulb!.Value);
            Assert.That(data.LightRadius, Is.EqualTo(7.5f));
            Assert.That(data.LightEnergy, Is.EqualTo(.9f));
            Assert.That(data.Color, Is.EqualTo(Color.FromHex("#DEEFFF")));
            var point = server.GetComponent<PointLightComponent>(cold);
            Assert.That(point.Enabled, Is.True);
            Assert.That(point.Radius, Is.EqualTo(7.5f));
            Assert.That(point.Energy, Is.EqualTo(.9f));
            Assert.That(server.GetComponent<PointLightComponent>(broken).Enabled, Is.False);
            Assert.That(server.GetComponent<LightBulbComponent>(lights.GetBulb(broken)!.Value).State, Is.EqualTo(LightBulbState.Broken));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var grid = client.GetEntity(server.GetNetEntity(map.Grid));
            var source = client.GetComponent<BitrunAreaLightingComponent>(grid);
            Assert.That(source.Tiles[new(0, 0)], Is.EqualTo(Color.White), "Authored fullbright areas stay fullbright.");
            Assert.That(source.Tiles[new(1, 0)], Is.EqualTo(Color.FromHex("#8589FA")));
            Assert.That(source.Tiles.GetValueOrDefault(new(2, 0), Color.Black), Is.EqualTo(Color.Black),
                "Unlit cells receive no ambient light.");
        });
        await pair.Server.WaitAssertion(() => Assert.That(server.System<SharedPoweredLightSystem>().EjectBulb(cold), Is.Not.Null));
        await pair.RunSeconds(.2f);
        await pair.Server.WaitAssertion(() => Assert.That(server.GetComponent<PointLightComponent>(cold).Enabled, Is.False,
            "Removing the source bulb must extinguish the actual light, not just its sprite."));
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}
