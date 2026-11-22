using System.IO;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Events;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class BankAmountSerializationTests
{
    [Test]
    public void MarketCartCanChargeMoreThanInt32WithoutOverflow()
    {
        var lots = new[]
        {
            new Content.Server._NF.Market.MarketLot { Price = 1_000_000_000, Quantity = 3 },
            new Content.Server._NF.Market.MarketLot { Price = 500_000_000, Quantity = 2 },
        };
        Assert.That(Content.Server._NF.Market.Extensions.MarketDataExtensions.GetMarketValue(lots, 1), Is.EqualTo(4_000_000_000L));
    }

    [TestCase(3_000_000_000L)]
    [TestCase(9_007_199_254_740_993L)]
    [TestCase(long.MaxValue)]
    public void AtmStateAndRequestsRoundTripWithoutTruncation(long amount)
    {
        var serializer = new NetSerializer.Serializer(new[]
        {
            typeof(BankATMMenuInterfaceState), typeof(StationBankATMMenuInterfaceState),
            typeof(BankWithdrawMessage), typeof(StationBankWithdrawMessage), typeof(StationBankDepositMessage),
        });
        using var stream = new MemoryStream();
        serializer.Serialize(stream, new BankATMMenuInterfaceState(amount, true, 1));
        serializer.Serialize(stream, new StationBankATMMenuInterfaceState(amount, true, 1));
        serializer.Serialize(stream, new BankWithdrawMessage(amount));
        serializer.Serialize(stream, new StationBankWithdrawMessage(amount, "payroll", "test"));
        serializer.Serialize(stream, new StationBankDepositMessage(amount, "donation", "test"));
        stream.Position = 0;
        Assert.That(((BankATMMenuInterfaceState)serializer.Deserialize(stream)).Balance, Is.EqualTo(amount));
        Assert.That(((StationBankATMMenuInterfaceState)serializer.Deserialize(stream)).Balance, Is.EqualTo(amount));
        Assert.That(((BankWithdrawMessage)serializer.Deserialize(stream)).Amount, Is.EqualTo(amount));
        Assert.That(((StationBankWithdrawMessage)serializer.Deserialize(stream)).Amount, Is.EqualTo(amount));
        Assert.That(((StationBankDepositMessage)serializer.Deserialize(stream)).Amount, Is.EqualTo(amount));
    }
}
