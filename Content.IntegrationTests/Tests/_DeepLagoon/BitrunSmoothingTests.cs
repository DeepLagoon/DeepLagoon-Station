using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client.IconSmoothing;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunSmoothingTests
{
    private static readonly (int Bit, Vector2 Offset)[] Neighbours =
    {
        (1, new(0, 1)), (2, new(0, -1)), (4, new(1, 0)), (8, new(-1, 0)),
        (16, new(1, 1)), (32, new(1, -1)), (64, new(-1, -1)), (128, new(-1, 1)),
    };

    private static bool ValidMask(int mask) =>
        (mask & 16) == 0 || (mask & 5) == 5;

    private static IEnumerable<int> Masks() => Enumerable.Range(0, 256).Where(mask =>
        ValidMask(mask) && ((mask & 32) == 0 || (mask & 6) == 6) &&
        ((mask & 64) == 0 || (mask & 10) == 10) && ((mask & 128) == 0 || (mask & 9) == 9));

    [Test]
    public async Task ImportedSandbagsFollowAllJunctionsAndUpdateWhenNeighboursChange()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        EntityUid center = default, wall = default;
        var neighbours = new List<EntityUid>();
        var position = map.GridCoords.Offset(new Vector2(.5f, .5f));
        await pair.Server.WaitAssertion(() =>
        {
            var maps = server.System<SharedMapSystem>();
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(map.Grid, map.Grid.Comp, (from x in Enumerable.Range(-3, 7)
                from y in Enumerable.Range(-3, 7) select (new Vector2i(x, y), tile)).ToList());
            var observer = server.SpawnEntity("MobObserver", position);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
            center = server.SpawnEntity("BitrunMapStructureBarricadeSandbags", position);
            Assert.That(server.GetComponent<TransformComponent>(center).Anchored, Is.True);
        });
        await pair.RunTicksSync(20);
        var netCenter = server.GetNetEntity(center);

        async Task Check(int mask)
        {
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var client = pair.Client.EntMan;
                client.System<IconSmoothSystem>().FrameUpdate(0);
                var uid = client.GetEntity(netCenter);
                var sprite = client.GetComponent<SpriteComponent>(uid);
                var layer = sprite.AllLayers.First();
                Assert.That(layer.RsiState.Name, Is.EqualTo($"sandbags-{mask}"));
                Assert.That(layer.ActualRsi.TryGetState(layer.RsiState, out _), Is.True);
            });
        }

        Assert.That(Masks().Count(), Is.EqualTo(47));
        foreach (var mask in Masks())
        {
            await pair.Server.WaitAssertion(() =>
            {
                foreach (var uid in neighbours)
                    server.DeleteEntity(uid);
                neighbours.Clear();
                foreach (var (bit, offset) in Neighbours.Where(n => (mask & n.Bit) != 0))
                    neighbours.Add(server.SpawnEntity("BitrunMapStructureBarricadeSandbags", position.Offset(offset)));
            });
            await Check(mask);
        }

        EntityUid diagonal = default;
        await pair.Server.WaitAssertion(() =>
        {
            foreach (var uid in neighbours)
                server.DeleteEntity(uid);
            neighbours.Clear();
            diagonal = server.SpawnEntity("BitrunMapStructureBarricadeSandbags", position.Offset(new Vector2(1, 1)));
            neighbours.Add(diagonal);
        });
        await Check(0);
        await pair.Server.WaitAssertion(() => neighbours.Add(server.SpawnEntity("BitrunMapStructureBarricadeSandbags", position.Offset(new Vector2(0, 1)))));
        await Check(1);
        await pair.Server.WaitAssertion(() => neighbours.Add(server.SpawnEntity("BitrunMapStructureBarricadeSandbags", position.Offset(new Vector2(1, 0)))));
        await Check(21);
        await pair.Server.WaitAssertion(() =>
        {
            server.DeleteEntity(diagonal);
            neighbours.Remove(diagonal);
        });
        await Check(5);
        await pair.Server.WaitAssertion(() =>
        {
            foreach (var uid in neighbours)
                server.DeleteEntity(uid);
            wall = server.SpawnEntity("BitrunMapClosedWall", position.Offset(new Vector2(0, 1)));
        });
        await Check(1);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var uid = client.GetEntity(server.GetNetEntity(wall));
            var smooth = client.GetComponent<IconSmoothComponent>(uid);
            Assert.That(client.GetComponent<SpriteComponent>(uid).AllLayers.First().RsiState.Name,
                Is.EqualTo(smooth.StateBase + "0"), "Walls do not join sandbags: source group matching is asymmetric.");
        });
        await pair.Server.WaitAssertion(() =>
        {
            var transforms = server.System<SharedTransformSystem>();
            var transform = server.GetComponent<TransformComponent>(wall);
            transforms.Unanchor(wall, transform);
            transforms.SetCoordinates(wall, transform, position.Offset(new Vector2(1, 0)));
            transforms.AnchorEntity(wall, transform);
        });
        await Check(4);
        await pair.Server.WaitAssertion(() => server.System<SharedTransformSystem>().Unanchor(wall, server.GetComponent<TransformComponent>(wall)));
        await Check(0);
        await pair.Server.WaitAssertion(() => server.System<SharedTransformSystem>().AnchorEntity(wall, server.GetComponent<TransformComponent>(wall)));
        await Check(4);
        await pair.Server.WaitAssertion(() => server.DeleteEntity(wall));
        await Check(0);

        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var checkedPrototypes = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(p =>
                         p.ID.StartsWith("BitrunMap") && p.Components.TryGetValue("IconSmooth", out var smooth) &&
                         smooth.Component is IconSmoothComponent { Mode: IconSmoothingMode.Bitmask8 }))
            {
                var uid = client.SpawnEntity(prototype.ID, client.GetComponent<TransformComponent>(client.GetEntity(netCenter)).Coordinates);
                var smooth = client.GetComponent<IconSmoothComponent>(uid);
                if (prototype.ID == "BitrunMapStructureSpiderStickyweb")
                {
                    var appearance = client.System<AppearanceSystem>();
                    appearance.SetData(uid, Content.Shared.Spider.SpiderWebVisuals.Variant, 2);
                    var data = new Entity<AppearanceComponent>(uid, client.GetComponent<AppearanceComponent>(uid));
                    appearance.OnChangeData(data, client.GetComponent<SpriteComponent>(uid));
                }
                var layer = client.GetComponent<SpriteComponent>(uid).AllLayers.First();
                foreach (var mask in Masks())
                    Assert.That(layer.ActualRsi.TryGetState(smooth.StateBase + mask, out _), Is.True,
                        $"{prototype.ID} is missing source junction {mask}.");
                checkedPrototypes++;
                client.DeleteEntity(uid);
            }
            Assert.That(checkedPrototypes, Is.GreaterThan(50));
        });
        await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}
