#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Decals;
using Content.Server.Procedural;
using Content.Shared.Doors.Components;
using Content.Shared.Procedural;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExpeditionDungeonPostGenerationTests
{
    [TestCase("NFSalvageOutpost", 202611201)]
    [TestCase("NFMineshaft", 202611203)]
    public async Task NativeSeededDungeonBuildsItsJunctionsAndVisibleCorridorSkirting(string prototype, int seed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        Entity<MapGridComponent> grid = default;
        Task<List<Dungeon>>? job = null;
        await pair.Server.WaitAssertion(() =>
        {
            var maps = em.System<SharedMapSystem>();
            maps.CreateMap(out var mapId, runMapInit: true);
            grid = maps.CreateGridEntity(mapId);
            var config = pair.Server.ProtoMan.Index<DungeonConfigPrototype>(prototype);
            if (prototype == "NFSalvageOutpost")
                Assert.That(config.Data.SpawnGroups[DungeonDataKey.Junction].Id, Is.EqualTo("NFMiningAirlock"));
            else
                Assert.That(config.Data.Colors[DungeonDataKey.Decals], Is.EqualTo(
                    pair.Server.ProtoMan.Index<DungeonConfigPrototype>("NFCaveFactory").Data.Colors[DungeonDataKey.Decals]),
                    "The mine uses the existing opaque cave-factory corridor color.");

            // Only the dungeon's own seeded RNG is supplied; global gameplay RNG is left alone.
            job = em.System<DungeonSystem>().GenerateDungeonAsync(config, config.ID, grid.Owner, grid.Comp, Vector2i.Zero, seed);
        });

        for (var tick = 0; tick < 1800 && !job!.IsCompleted; tick++)
            await pair.RunTicksSync(1);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(job!.IsCompleted, Is.True, "The native dungeon job must finish within the bounded test run.");
            var dungeons = job.GetAwaiter().GetResult();
            Assert.That(dungeons, Is.Not.Empty);
            Assert.That(dungeons.Sum(d => d.Rooms.Count), Is.GreaterThan(0));
            Assert.That(dungeons.Sum(d => d.CorridorTiles.Count), Is.GreaterThan(0));
            var color = pair.Server.ProtoMan.Index<DungeonConfigPrototype>(prototype).Data.Colors[DungeonDataKey.Decals];
            var corridors = dungeons.SelectMany(d => d.CorridorTiles).ToHashSet();
            var roomTiles = dungeons.SelectMany(d => d.RoomTiles).ToHashSet();
            var roomEntrances = dungeons.SelectMany(d => d.Entrances).ToHashSet();

            var skirting = em.System<DecalSystem>().GetDecalsIntersecting(grid.Owner, grid.Comp.LocalAABB)
                .Select(entry => entry.Decal)
                .Where(decal => decal.Id.StartsWith("BrickTileWhite", StringComparison.Ordinal) &&
                                corridors.Contains(decal.Coordinates.Floored()) && decal.Color == color).ToArray();
            Assert.That(color.A, Is.GreaterThan(0f));
            Assert.That(skirting, Is.Not.Empty, "Post-generation must create visible colored corridor decals, not merely suppress its error.");

            if (prototype == "NFSalvageOutpost")
            {
                var junctionDoors = 0;
                var doors = em.EntityQueryEnumerator<DoorComponent, TransformComponent, MetaDataComponent>();
                while (doors.MoveNext(out _, out _, out var transform, out var metadata))
                {
                    var tile = transform.LocalPosition.Floored();
                    if (transform.GridUid == grid.Owner && metadata.EntityPrototype?.ID == "AirlockMiningGlassDungeon" &&
                        corridors.Contains(tile) && !roomTiles.Contains(tile) && !roomEntrances.Contains(tile))
                        junctionDoors++;
                }
                Assert.That(junctionDoors, Is.GreaterThan(0),
                    "The seeded outpost needs mining-glass junction doors in its corridors, outside prefab rooms/room entrances.");
            }
        });
        await pair.CleanReturnAsync();
    }
}
