using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Procurement;
using Content.Server._NF.Market.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Shared.Cargo.Events;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ProcurementShadowTests
{
    [Test]
    public async Task ShadowCapturesRealAppraisalsButDoesNotChangeSaleOrIssueCredit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        foreach (var enabled in new[] { false, true })
        {
            var map = await pair.CreateTestMap();
            ProcurementSystem observer = null!;
            var before = 0L;
            await pair.Server.WaitAssertion(() =>
            {
                var e = pair.Server.EntMan;
                observer = e.System<ProcurementSystem>();
                pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(ProcurementCVars.ShadowEnabled, enabled);
                before = observer.RecordedSales;
                var coords = map.GridCoords.Offset(new Vector2(.5f, .5f));
                var station = e.SpawnEntity(null, coords);
                e.AddComponent<CargoMarketDataComponent>(station);
                e.EnsureComponent<StationMemberComponent>(map.Grid).Station = station;
                var console = e.SpawnEntity("ComputerPalletConsoleNFVeryHighMarket", coords);
                var pallet = e.SpawnEntity("CargoPalletSell", coords);
                Assert.That(e.System<SharedTransformSystem>().AnchorEntity(console), Is.True);
                Assert.That(e.System<SharedTransformSystem>().AnchorEntity(pallet), Is.True);
                var material = e.SpawnEntity("SheetPlasteel", coords);
                e.System<StackSystem>().SetCount(material, 3);
                var values = new Dictionary<int, ProcurementValuation>();
                var payout = e.System<PricingSystem>().GetPalletPrice(material, map.Grid, 1.5, out var price,
                    observeOwnPrice: (entity, basePrice, paid) => values[entity.Id] = new(basePrice, paid));
                if (enabled)
                {
                    var capture = observer.CapturePallet(console, map.Grid, null,
                        [new(material.Id, price, payout, ProcurementCategory.Unclassified)], values, (long)payout, "Credit");
                    Assert.That(capture, Is.Not.Null);
                    Assert.That(capture!.Items.Single(i => i.Entity == material.Id).Quantity, Is.EqualTo(3));
                    Assert.That(capture.Items.Single(i => i.Entity == material.Id).OwnPayout, Is.EqualTo(payout));
                    Assert.That(capture.Roots.Single().SuggestedCategory, Is.EqualTo(ProcurementCategory.Materials));
                    Assert.That(capture.ManifestComplete, Is.True);
                }
                e.EventBus.RaiseLocalEvent(console, new CargoPalletSellMessage());
                Assert.That(e.EntityExists(material), Is.False);
                var paid = e.EntityQuery<StackComponent, TransformComponent>()
                    .Where(x => x.Item1.StackTypeId == "Credit" && x.Item2.GridUid == map.Grid)
                    .Sum(x => (long)x.Item1.Count);
                Assert.That(paid, Is.EqualTo((long)payout), "The observer must not reject, debit or reprice the sale.");
            });
            Task flush = Task.CompletedTask;
            await pair.Server.WaitAssertion(() => flush = observer.FlushAsync());
            await flush;
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(observer.RecordedSales, Is.EqualTo(before + (enabled ? 1 : 0)));
                Assert.That(observer.Status, Does.Contain("fault=none"));
                pair.Server.EntMan.DeleteEntity(map.MapUid);
            });
        }
        await pair.Server.WaitAssertion(() => pair.Server.ResolveDependency<IConfigurationManager>()
            .SetCVar(ProcurementCVars.ShadowEnabled, false));
        await pair.CleanReturnAsync();
    }
}
