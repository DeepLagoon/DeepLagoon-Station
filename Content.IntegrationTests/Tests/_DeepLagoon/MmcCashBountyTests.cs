using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NF.Market.Components;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Labels;
using Content.Server.Stack;
using Content.Server.Storage.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Events;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MmcCashBountyTests
{
    [Test]
    public async Task CashOrdersPreservePrincipalRejectSurplusAndCannotPayTwice()
    {
        // Each scenario has its own map; one engine pair avoids eleven content boots.
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var cases = new List<(string Name, Action<IEntityManager, EntityUid, EntityCoordinates> Run)>
        {
            ("T1", (e, grid, coords) => ExactCash(e, grid, coords, "MMCBountySpaceCashT1", 200000, 100)),
            ("T2", (e, grid, coords) => ExactCash(e, grid, coords, "MMCBountySpaceCashT2", 400000, 400)),
            ("T3", (e, grid, coords) => ExactCash(e, grid, coords, "MMCBountySpaceCashT3", 800000, 800)),
            ("underpayment", (e, grid, coords) => IncorrectAmount(e, grid, coords, 399999)),
            ("one surplus credit", (e, grid, coords) => IncorrectAmount(e, grid, coords, 400001)),
            ("510 million", (e, grid, coords) => IncorrectAmount(e, grid, coords, 510000000)),
            ("exact nested cash", (e, grid, coords) => NestedCash(e, grid, coords, 0)),
            ("surplus nested cash", (e, grid, coords) => NestedCash(e, grid, coords, 1)),
            ("integer overflow", (e, grid, coords) => NestedCash(e, grid, coords, int.MaxValue)),
            ("duplicate labels", DuplicateLabels),
            ("expired bounty and loose cash", InactiveBounty),
        };
        foreach (var scenario in cases)
        {
            var map = await pair.CreateTestMap();
            await pair.Server.WaitAssertion(() =>
            {
                TestContext.Progress.WriteLine($"MMC regression: {scenario.Name}");
                try
                {
                    scenario.Run(pair.Server.EntMan, map.Grid, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
                }
                finally
                {
                    pair.Server.EntMan.DeleteEntity(map.MapUid);
                }
            });
        }
        await pair.CleanReturnAsync();
    }

    private static void ExactCash(IEntityManager e, EntityUid grid, EntityCoordinates coords, string bounty, int required, int reward)
    {
        var sale = Setup(e, grid, coords, bounty);
        var crate = Crate(e, coords, sale.Station, sale.Database.Bounties.Single(), required);
        Assert.That(e.System<CargoSystem>().IsBountyComplete(crate, out _), Is.True);

        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());

        Assert.That(e.EntityExists(sale.Console), Is.True);
        Assert.That(e.EntityExists(crate), Is.False);
        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(required + 75L),
            "Only the 50-credit crate receives the 1.5 market modifier; principal must not be multiplied.");
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.EqualTo(reward));
        Assert.That(sale.Database.History, Has.Count.EqualTo(1));
        Assert.That(e.GetComponent<CargoMarketDataComponent>(sale.Station).MarketDataList, Is.Empty);

        // Collect the reward before another sale: MIC is itself a sellable
        // commodity, so leaving it on the pallet would be a different transaction.
        var rewards = e.EntityQuery<StackComponent, TransformComponent>()
            .Where(c => c.Item1.StackTypeId == "MMCIndustrialCredit" && c.Item2.GridUid == grid)
            .Select(c => c.Item1.Owner).ToArray();
        var away = new EntityCoordinates(e.GetComponent<TransformComponent>(grid).ParentUid, 10, 10);
        foreach (var token in rewards)
            e.System<SharedTransformSystem>().SetCoordinates(token, away);

        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());
        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(required + 75L));
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.Zero);
        Assert.That(rewards.Sum(token => e.GetComponent<StackComponent>(token).Count), Is.EqualTo(reward));
        Assert.That(sale.Database.History, Has.Count.EqualTo(1));
    }

    private static void IncorrectAmount(IEntityManager e, EntityUid grid, EntityCoordinates coords, int amount)
    {
        var sale = Setup(e, grid, coords, "MMCBountySpaceCashT2");
        var crate = Crate(e, coords, sale.Station, sale.Database.Bounties.Single(), amount);

        Assert.That(e.System<CargoSystem>().IsBountyComplete(crate, out _), Is.False);
        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());

        Assert.That(e.EntityExists(crate), Is.True);
        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(amount));
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.Zero);
        Assert.That(sale.Database.History, Is.Empty);
        Assert.That(sale.Database.Bounties, Has.Count.EqualTo(1));
    }

    private static void NestedCash(IEntityManager e, EntityUid grid, EntityCoordinates coords, int surplus)
    {
        var sale = Setup(e, grid, coords, "MMCBountySpaceCashT2");
        var crate = Crate(e, coords, sale.Station, sale.Database.Bounties.Single(), 200000);
        var inner = e.SpawnEntity("CrateGenericSteel", coords);
        var containers = e.System<SharedContainerSystem>();
        Assert.That(containers.Insert(inner, e.GetComponent<EntityStorageComponent>(crate).Contents), Is.True);
        var cash = e.SpawnEntity("SpaceCash", coords);
        e.System<StackSystem>().SetCount(cash, 200000);
        Assert.That(containers.Insert(cash, e.GetComponent<EntityStorageComponent>(inner).Contents), Is.True);
        if (surplus > 0)
        {
            var extra = e.SpawnEntity("SpaceCash", coords);
            e.System<StackSystem>().SetCount(extra, surplus);
            Assert.That(containers.Insert(extra, e.GetComponent<EntityStorageComponent>(inner).Contents), Is.True);
        }

        Assert.That(e.System<CargoSystem>().IsBountyComplete(crate, out _), Is.EqualTo(surplus == 0));
        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());

        Assert.That(e.EntityExists(crate), Is.EqualTo(surplus != 0));
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.EqualTo(surplus == 0 ? 400 : 0));
        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(surplus == 0 ? 400150L : 400000L + surplus));
    }

    private static void DuplicateLabels(IEntityManager e, EntityUid grid, EntityCoordinates coords)
    {
        var sale = Setup(e, grid, coords, "MMCBountySpaceCashT2");
        var bounty = sale.Database.Bounties.Single();
        Crate(e, coords, sale.Station, bounty, 400000);
        Crate(e, coords, sale.Station, bounty, 400000);

        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());

        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(800150));
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.EqualTo(400));
        Assert.That(sale.Database.History, Has.Count.EqualTo(1));
    }

    private static void InactiveBounty(IEntityManager e, EntityUid grid, EntityCoordinates coords)
    {
        var sale = Setup(e, grid, coords, "MMCBountySpaceCashT2");
        var crate = Crate(e, coords, sale.Station, sale.Database.Bounties.Single(), 400000);
        sale.Database.Bounties.Clear();
        var looseCash = e.SpawnEntity("SpaceCash", coords);
        e.System<StackSystem>().SetCount(looseCash, 1000000);

        e.EventBus.RaiseLocalEvent(sale.Console, new CargoPalletSellMessage());

        Assert.That(e.EntityExists(crate), Is.True);
        Assert.That(e.EntityExists(looseCash), Is.True);
        Assert.That(Currency(e, grid, "Credit"), Is.EqualTo(1400000));
        Assert.That(Currency(e, grid, "MMCIndustrialCredit"), Is.Zero);
    }

    private static (EntityUid Station, EntityUid Console, StationCargoBountyDatabaseComponent Database) Setup(
        IEntityManager e, EntityUid grid, EntityCoordinates coordinates, string bounty)
    {
        var station = e.SpawnEntity(null, coordinates);
        e.AddComponent<CargoMarketDataComponent>(station);
        var database = e.AddComponent<StationCargoBountyDatabaseComponent>(station);
        e.EnsureComponent<StationMemberComponent>(grid).Station = station;
        Assert.That(e.System<CargoSystem>().TryAddBounty(station, bounty, database), Is.True);
        var pallet = e.SpawnEntity("CargoPalletSell", coordinates);
        var console = e.SpawnEntity("ComputerPalletConsoleNFVeryHighMarket", coordinates);
        // Use installed equipment, as on the live station, so the selling area
        // cannot include the sale console itself.
        var transforms = e.System<SharedTransformSystem>();
        Assert.That(transforms.AnchorEntity(pallet), Is.True);
        Assert.That(transforms.AnchorEntity(console), Is.True);
        return (station, console, database);
    }

    private static EntityUid Crate(IEntityManager e, EntityCoordinates coordinates, EntityUid station,
        CargoBountyData bounty, int amount)
    {
        var crate = e.SpawnEntity("CrateGenericSteel", coordinates);
        var label = e.SpawnEntity("PaperCargoBountyManifest", coordinates);
        e.System<CargoSystem>().SetupBountyLabel(label, station, bounty);
        Assert.That(e.System<ItemSlotsSystem>().TryInsert(crate, LabelSystem.ContainerName, label, null), Is.True);
        var cash = e.SpawnEntity("SpaceCash", coordinates);
        e.System<StackSystem>().SetCount(cash, amount);
        Assert.That(e.System<SharedContainerSystem>().Insert(cash, e.GetComponent<EntityStorageComponent>(crate).Contents), Is.True);
        return crate;
    }

    private static long Currency(IEntityManager e, EntityUid grid, string stack)
        => e.EntityQuery<StackComponent, TransformComponent>()
            .Where(c => c.Item1.StackTypeId == stack && c.Item2.GridUid == grid)
            .Sum(c => (long) c.Item1.Count);
}
