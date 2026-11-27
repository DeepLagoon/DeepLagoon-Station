#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Cargo.Systems;
using Content.Server._NF.Shipyard.Systems;
using Content.Shared.Cargo.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkedAppraisalStepsTests
{
    private sealed class AppraisalPriceProbeSystem : EntitySystem
    {
        public int Calculations;
        public override void Initialize()
        {
            SubscribeLocalEvent<StaticPriceComponent, PriceCalculationEvent>((EntityUid uid, StaticPriceComponent _, ref PriceCalculationEvent args) =>
            {
                Calculations++;
            });
        }
    }

    [TestCase(0.01)]
    [TestCase(1234.56789)]
    [TestCase(10000000000.0)]
    public async Task CooperativeValuationMatchesOrdinaryHullAndNestedContents(double price)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var pricing = entities.System<PricingSystem>();
            var shipyard = entities.System<ShipyardSystem>();
            var probe = entities.System<AppraisalPriceProbeSystem>();
            var containers = entities.System<SharedContainerSystem>();
            var root = Item(map.Grid.Owner, price);
            var nested = Item(map.Grid.Owner, price / 3);
            var leaf = Item(map.Grid.Owner, price / 7);
            Item(map.Grid.Owner, price / 11);
            Assert.That(containers.Insert(nested, containers.EnsureContainer<Container>(root, "appraisal-main")), Is.True);
            Assert.That(containers.Insert(leaf, containers.EnsureContainer<Container>(nested, "appraisal-nested")), Is.True);
            // Arbitrary transform descendants are deliberately excluded by ordinary AppraiseGrid.
            Item(root, 777777777);

            var expected = pricing.AppraiseGrid(map.Grid.Owner);
            double? actual = null;
            var steps = 0;
            using (var work = pricing.AppraiseGridSteps(map.Grid.Owner, value => actual = value).GetEnumerator())
            {
                while (true)
                {
                    var before = probe.Calculations;
                    var more = work.MoveNext();
                    Assert.That(probe.Calculations - before, Is.LessThanOrEqualTo(1), "One step must not traverse an entire loaded inventory.");
                    if (!more) break;
                    Assert.That(actual, Is.Null, "A partial appraisal cannot become a payable price.");
                    Assert.That(++steps, Is.LessThan(100));
                }
            }
            Assert.That(actual, Is.EqualTo(expected), "Addition grouping must match the synchronous double result exactly.");
            int? sale = null;
            foreach (var _ in shipyard.AppraiseParkedShuttleSteps(map.Grid.Owner, value => sale = value)) { }
            Assert.That(sale, Is.EqualTo(shipyard.AppraiseParkedShuttle(map.Grid.Owner)), "Use the same current sale multiplier, integer truncation and clamp.");

            actual = null;
            foreach (var _ in pricing.AppraiseGridSteps(map.Grid.Owner, value => actual = value, uid => uid != nested)) { }
            Assert.That(actual, Is.EqualTo(pricing.AppraiseGrid(map.Grid.Owner, uid => uid != nested)), "A rejected container excludes its full subtree.");

            EntityUid Item(EntityUid parent, double value)
            {
                var item = entities.SpawnEntity(null, new EntityCoordinates(parent, Vector2.Zero));
                entities.EnsureComponent<StaticPriceComponent>(item).Price = value;
                return item;
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CancellingAppraisalNeverPublishesAPrice()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var item = entities.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, Vector2.Zero));
            entities.EnsureComponent<StaticPriceComponent>(item).Price = 1234;
            var completed = false;
            using (var steps = entities.System<ShipyardSystem>().AppraiseParkedShuttleSteps(map.Grid.Owner, _ => completed = true).GetEnumerator())
                Assert.That(steps.MoveNext(), Is.True);
            Assert.That(completed, Is.False);
            Assert.That(entities.EntityExists(item), Is.True, "Cancellation does not own or mutate the priced grid.");
        });
        await pair.CleanReturnAsync();
    }
}
