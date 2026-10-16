using System.Collections.Generic;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Events;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class PhysicsSafetyMapTests
{
    private sealed class RejectedFixtureListener : EntitySystem
    {
        public readonly List<string> Rejected = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<InvalidPhysicsFixtureEvent>(OnRejected);
        }

        private void OnRejected(ref InvalidPhysicsFixtureEvent ev)
        {
            Rejected.Add($"{ev.Entity}: {ev.FixtureId}: {ev.Reason}");
        }
    }

    [TestCase("/Maps/_Mono/Outpost/caelestinus_central.yml", false)]
    [TestCase("/Maps/_DeepLagoon/Apartments/residential_stop.yml", true)]
    public async Task MapLoadsWithoutQuarantiningValidEntities(string path, bool isGrid)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var entities = server.EntMan;
        await server.WaitAssertion(() =>
        {
            var rejected = entities.System<RejectedFixtureListener>().Rejected;
            rejected.Clear();
            var loader = entities.System<MapLoaderSystem>();
            if (isGrid)
            {
                entities.System<SharedMapSystem>().CreateMap(out var mapId);
                Assert.That(loader.TryLoadGrid(mapId, new ResPath(path), out var grid), Is.True);
                Assert.That(grid, Is.Not.Null);
            }
            else
            {
                Assert.That(loader.TryLoadMap(new ResPath(path), out var map, out var grids), Is.True);
                Assert.That(map, Is.Not.Null);
                Assert.That(grids, Is.Not.Empty);
            }
            Assert.That(rejected, Is.Empty, string.Join('\n', rejected));
        });
        await pair.CleanReturnAsync();
    }
}
