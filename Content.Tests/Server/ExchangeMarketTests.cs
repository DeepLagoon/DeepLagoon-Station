using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Server.Database;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ExchangeMarketTests
{
    [Test]
    public void InitialOfferingHasARealAskButNoSpessosToPayFarmers()
    {
        var market = ExchangeMarket.Create(0);
        Assert.That(market.Spot("contrib"), Is.EqualTo(10000));
        var quote = market.Quote("contrib", 1000);
        Assert.That(quote.BuyCost, Is.GreaterThan(10000));
        Assert.That(quote.CanSell, Is.False);
        Assert.Throws<InvalidOperationException>(() => market.Trade("contrib", 1000, false, 0, 1000, 0, 1));
        Assert.That(market.Pools.Values.Sum(p => p.Cash), Is.Zero);
    }

    [Test]
    public void BuyingWithoutAMinerAndSellingCannotCreateMoneyOrAProfitableRoundTrip()
    {
        var market = ExchangeMarket.Create(0);
        long bank = 1000000;
        var buy = market.Quote("contrib", 1000);
        bank += market.Trade("contrib", 1000, true, buy.BuyCost, 0, bank, 1).BankChange;
        Assert.That(market.Spot("contrib"), Is.GreaterThan(10000));
        bank += market.Trade("contrib", 1000, false, 0, 1000, bank, 2).BankChange;
        Assert.That(bank, Is.LessThan(1000000));
        Assert.That(bank + market.Pools.Values.Sum(p => p.Cash + p.FeesBurned), Is.EqualTo(1000000));
        Assert.That(market.Pools["contrib"].Tokens, Is.EqualTo(10000000));
    }

    [Test]
    public void SlippageGuardAndInsufficientBalancesLeaveThePoolUntouched()
    {
        var market = ExchangeMarket.Create(0);
        var original = JsonSerializer.Serialize(market);
        Assert.Throws<InvalidOperationException>(() => market.Trade("contrib", 1000, true, 1, 0, 1000000, 1));
        Assert.Throws<InvalidOperationException>(() => market.Trade("contrib", 1000, true, long.MaxValue, 0, 1, 1));
        Assert.That(JsonSerializer.Serialize(market), Is.EqualTo(original));
    }

    [Test]
    public void BuyingOneAssetDoesNotCreateBuybackFundsForAnother()
    {
        var market = ExchangeMarket.Create(0);
        var ask = market.Quote("contrib", 1000);
        market.Trade("contrib", 1000, true, ask.BuyCost, 0, 1000000, 1);
        Assert.That(market.Quote("contrib", 1000).CanSell, Is.True);
        Assert.That(market.Quote("goida", 1000).CanSell, Is.False);
    }

    [Test]
    public void FractionalOrdersAndRequestBoundsAreExplicit()
    {
        var market = ExchangeMarket.Create(0);
        Assert.That(market.Quote("contrib", 1).BuyCost, Is.GreaterThan(0));
        Assert.Throws<ArgumentException>(() => market.Quote("contrib", 0));
        Assert.Throws<ArgumentException>(() => market.Quote("contrib", -1));
        Assert.Throws<ArgumentException>(() => market.Quote("contrib", ExchangeMarket.MaxOrderUnits + 1));
        Assert.Throws<ArgumentException>(() => market.Quote("invented", 1));
    }

    [Test]
    public void AllFarmsShareThirtyCoinsAnHourAndCanCarryAtMostTwoHours()
    {
        var market = ExchangeMarket.Create(0);
        Assert.That(market.Mint(64, 0), Is.Empty);
        Assert.That(market.Mint(64, 3600000), Has.Length.EqualTo(30));
        Assert.That(market.Mint(64, 3600000), Is.Empty, "More farms at the same time do not issue another block.");
        Assert.That(market.Mint(64, 3600000L * 1000), Has.Length.EqualTo(60));
        Assert.That(market.Mint(64, 3600000L * 1000), Is.Empty);
    }

    [Test]
    public void MiningDoesNotCreateBankLiquidityAndTheIssueBudgetSurvivesReload()
    {
        var market = ExchangeMarket.Create(0);
        market.Mint(64, 3600000);
        var restored = JsonSerializer.Deserialize<ExchangeMarket>(JsonSerializer.Serialize(market))!;
        Assert.That(restored.Mint(64, 3600000), Is.Empty);
        Assert.That(restored.Pools.Values.Sum(p => p.Cash), Is.Zero);
    }

    [Test]
    public void ExceptionalMaterialSaleHasBoundedIndexImpactAndDoesNotChangeTradingPools()
    {
        var market = ExchangeMarket.Create(0);
        market.SampleMaterials([new("Steel", 100, 1000000, 1000000000)], 60000);
        Assert.That(market.Materials["Steel"].Single().Price, Is.EqualTo(110));
        Assert.That(market.Materials["ConstructionIndex"].Single().Price, Is.EqualTo(110));
        Assert.That(market.Pools.Values.Sum(p => p.Cash), Is.Zero);
        market.SampleMaterials([new("Steel", 100, null, 0)], 120000);
        Assert.That(market.Materials["Steel"][^1].Price, Is.LessThan(110));
    }

    [Test]
    public void HistoryKeepsRealObservationTimesAndVolumesWithinABoundedSevenDayWindow()
    {
        var market = ExchangeMarket.Create(0);
        market.Pools["contrib"].History = Enumerable.Range(0, 20000).Select(i => new ExchangeCandle(i * 60000L, 10000 + i, 1)).ToList();
        market.Sample("contrib", 20000L * 60000, 0);
        var history = market.Pools["contrib"].History;
        Assert.That(history.Count, Is.LessThan(1100));
        Assert.That(history[0].At, Is.GreaterThanOrEqualTo(20000L * 60000 - 604800000));
        Assert.That(history.Zip(history.Skip(1)).All(p => p.First.At < p.Second.At), Is.True);
        Assert.That(history.Sum(p => p.Volume), Is.EqualTo(10080));
        Assert.That(history[^1].At, Is.EqualTo(20000L * 60000));
    }
}
