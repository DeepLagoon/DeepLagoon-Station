using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Moq;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class OrbitalDebrisVisibilityCacheTests
{
    [Test]
    public async Task ViewersSharePositionSnapshotAndPublicChecksRefreshMovedPiles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var map = em.System<SharedMapSystem>().CreateMap();
            var debris = em.System<OrbitalDebrisSystem>();
            var transforms = em.System<SharedTransformSystem>();
            var piles = new List<EntityUid>();
            for (uint i = 0; i < 12; i++)
                piles.Add(debris.SpawnPile(map, new Vector2(1000 + i * 10, 1000), new Vector2(5000, 0), i + 713)!.Value);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(OrbitalDebrisSystem);
            var viewerType = type.GetNestedType("Viewer", BindingFlags.NonPublic)!;
            var active = (IList)type.GetField("_activeViewers", flags)!.GetValue(debris)!;
            var cache = (IList)type.GetField("_visibilityPiles", flags)!.GetValue(debris)!;
            var enforce = type.GetMethod("EnforceVisibleLimits", flags)!;
            var views = new List<List<OrbitalDebrisSystem.Observer>>();
            for (var i = 0; i < 65; i++)
            {
                var viewer = Activator.CreateInstance(viewerType, flags | BindingFlags.Public, null,
                    [new Mock<ICommonSession>().Object], null)!;
                var list = (List<OrbitalDebrisSystem.Observer>)viewerType.GetField("Views")!.GetValue(viewer)!;
                list.Add(new OrbitalDebrisSystem.Observer(map, new Vector2(50000, 50000), 200));
                views.Add(list);
                active.Add(viewer);
            }

            // Compare the two scheduling strategies on identical data; no timing assertion.
            enforce.Invoke(debris, null);
            var watch = Stopwatch.StartNew();
            for (var pass = 0; pass < 100; pass++)
            foreach (var view in views)
                debris.EnforceViewLimit(view);
            var separateMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (var pass = 0; pass < 100; pass++) enforce.Invoke(debris, null);
            var sharedMs = watch.Elapsed.TotalMilliseconds;
            TestContext.Out.WriteLine($"Visibility scheduling, 12 piles / 65 viewers / 100 passes: separate snapshots={separateMs:F3} ms; shared snapshot={sharedMs:F3} ms (comparison of strategies, not a production benchmark).");
            Assert.That(cache.Count, Is.EqualTo(12));
            Assert.That(debris.ActiveCount, Is.EqualTo(12));

            foreach (var view in views)
                view[0] = new OrbitalDebrisSystem.Observer(map, new Vector2(1050, 1000), 200);
            enforce.Invoke(debris, null);
            Assert.That(debris.ActiveCount, Is.EqualTo(5), "Later viewers must not count already retired cached entries.");
            var survivor = piles.First(uid => !em.IsQueuedForDeletion(uid));
            var destination = new Vector2(50000, 50000);
            transforms.SetWorldPosition(survivor, destination);
            debris.EnforceViewLimit(views[0]);
            var row = cache.Cast<object>().Single(value =>
                (EntityUid)value.GetType().GetProperty("Uid")!.GetValue(value)! == survivor);
            Assert.That(Vector2.Distance((Vector2)row.GetType().GetProperty("Position")!.GetValue(row)!, destination), Is.LessThan(.01f),
                "A public call in the same tick must sample the moved grid again.");
            em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
