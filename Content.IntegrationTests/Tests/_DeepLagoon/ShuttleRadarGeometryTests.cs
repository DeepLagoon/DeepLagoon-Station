using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Content.Client.Shuttles.UI;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleRadarGeometryTests
{
    [Test]
    public async Task RadarSharesMeshesCullsOffscreenRegionsAndSimplifiesDistantTerrain()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var maps = client.System<MapSystem>();
            var meshes = client.System<ShuttleRadarGeometrySystem>();
            var tiles = pair.Client.ResolveDependency<ITileDefinitionManager>();
            var map = maps.CreateMap(out _);
            var grid = maps.CreateGridEntity(map);
            grid.Comp.CanSplit = false;
            var changes = new List<(Vector2i, Tile)>();
            var floor = new Tile(tiles["Plating"].TileId);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                if ((x + y) % 2 == 0)
                    changes.Add((new Vector2i(x, y), floor));
            }
            // A disconnected grid section should stay unbuilt while outside the viewport.
            changes.Add((new Vector2i(100, 100), floor));
            maps.SetTiles(grid, changes);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var getMesh = typeof(ShuttleRadarGeometrySystem).GetMethod("GetVisibleMesh", flags)!;
            var view = new Box2(-1, -1, 33, 33);
            var detail = getMesh.Invoke(meshes, [grid, view, false])!;
            var regions = (IDictionary) detail.GetType().GetField("Regions")!.GetValue(detail)!;
            Assert.That(regions.Count, Is.EqualTo(5));
            Assert.That(GeometryCount(regions, "Detail"), Is.EqualTo(4));

            var sameGrid = getMesh.Invoke(meshes, [grid, view, false]);
            Assert.That(sameGrid, Is.SameAs(detail));
            var fullVertices = VertexCount(regions, "Detail");

            meshes.FrameUpdate(0);
            var overview = getMesh.Invoke(meshes, [grid, view, true]);
            Assert.That(overview, Is.SameAs(detail));
            Assert.That(GeometryCount(regions, "Overview"), Is.EqualTo(4));
            Assert.That(VertexCount(regions, "Overview"), Is.LessThan(fullVertices / 10));
            TestContext.Out.WriteLine($"Checkerboard fixture: {fullVertices} detail vertices, {VertexCount(regions, "Overview")} overview vertices.");

            // A changed boundary tile can change the outline on the other side of the seam.
            maps.SetTile(grid, new Vector2i(15, 1), Tile.Empty);
            Assert.That(IsDetailDirty(regions, new Vector2i(0, 0)), Is.True);
            Assert.That(IsDetailDirty(regions, new Vector2i(1, 0)), Is.True);
            Assert.That(IsDetailDirty(regions, new Vector2i(0, 1)), Is.False);
            client.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    private static bool IsDetailDirty(IDictionary regions, Vector2i index)
    {
        var region = regions[index]!;
        return (bool) region.GetType().GetField("Dirty")!.GetValue(region)!;
    }

    private static int GeometryCount(IDictionary regions, string field)
    {
        var count = 0;
        foreach (var region in regions.Values)
        {
            if (region.GetType().GetField(field)!.GetValue(region) != null)
                count++;
        }

        return count;
    }

    private static int VertexCount(IDictionary regions, string field)
    {
        var count = 0;
        foreach (var region in regions.Values)
        {
            if (region.GetType().GetField(field)!.GetValue(region) is { } geometry)
                count += (int) geometry.GetType().GetProperty("Count")!.GetValue(geometry)!;
        }

        return count;
    }
}
