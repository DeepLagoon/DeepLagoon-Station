using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server.Database;

public sealed record ExchangeAsset(string Id, string Symbol, long Anchor, string CoinPrototype);
public sealed record ExchangeCandle(long At, decimal Price, long Volume);
public sealed record ExchangeMaterialSample(string Id, decimal BasePrice, decimal? SoldPrice, long Volume, bool ConsumptionIndex = false);
public sealed record ExchangeQuote(long Units, long BuyCost, long SellProceeds, long BuyFee, long SellFee, bool CanSell);
public sealed record ExchangePosition(string Asset, long Units);
public sealed record ExchangeTradeRequest(Guid Operation, int Slot, string Asset, long Units, bool Buy, long Limit, int ExpectedProfileId = 0,
    bool Cash = false, RecoveryExternal? CashRecovery = null, string WalletAddress = "", Guid? WalletVersion = null);
public sealed record ExchangeResult(long Bank, int ProfileId, int Slot, ExchangeMarket Market,
    Dictionary<string, long> Wallet, bool Applied = false)
{
    public string Address { get; init; } = "";
    public Guid WalletVersion { get; init; }
    public bool NeedsPassword { get; init; }
    public string WalletCharacter { get; init; } = "";
    public ExchangeWalletInfo[] Wallets { get; init; } = [];
    public ExchangeWithdrawal[] Withdrawals { get; init; } = [];
}
public sealed record ExchangeWithdrawal(Guid Operation, long At, long Amount, string AccountOwner, bool Cash, string Asset, long Units);
public sealed record ExchangeWalletInfo(string Address, string Name, bool NeedsPassword);
public sealed record ExchangeWalletLogin(string Address, string Password, int ProfileId, bool Create = false);
public sealed record ExchangeWalletAccess(string Address, Guid Version);
public sealed record ExchangePasswordChange(string Address, int ProfileId, string CurrentPassword, string NewPassword);
public sealed record ExchangeRecipient(string Address, string Character, int ProfileId);
public sealed record ExchangeTransferRequest(Guid Operation, int Slot, int ProfileId, string Address, int RecipientProfileId,
    string Asset, long Units, string SenderAddress = "", Guid? WalletVersion = null);
public sealed record ExchangeAudit(Guid Operation, string Asset, long Units, string Address, long Before, long After,
    long Fee = 0, decimal Rate = 0, string Payment = "wallet", string? RecipientAddress = null,
    long? RecipientBefore = null, long? RecipientAfter = null, string? WalletCharacter = null);
public sealed record ExchangeMiningRequest(Guid Operation, Guid User, int ProfileId);
public sealed record ExchangeMiningResult(Guid Operation, string? Asset, long Units, bool Applied, string? Error = null);

/// <summary>Closed speso liquidity: trading moves existing player money; the market starts with zero cash.</summary>
public sealed class ExchangeMarket
{
    public const long UnitsPerCoin = 1000;
    public const long MaxOrderUnits = 1000000;
    public const long MintUnitsPerHour = 10000; // Shared by ALL farms for each asset, not per machine.
    public static readonly ExchangeAsset[] Assets =
    [
        new("contrib", "CTB", 10000, "BitcoinContribcoin"),
        new("ideas", "IDEA", 15000, "BitcoinIdeascoin"),
        new("goida", "GOI", 20000, "BitcoinGoidacoin"),
    ];

    public sealed class Pool
    {
        public long Tokens { get; set; } = 10000000; // Finite initial offering: 10,000 coins.
        public long Cash { get; set; }
        public long FeesBurned { get; set; }
        public decimal MintAllowance { get; set; }
        public long MintAt { get; set; }
        public long MinedUnits { get; set; }
        public List<ExchangeCandle> History { get; set; } = new();
    }
    public int Version { get; set; } = 1;
    public Dictionary<string, Pool> Pools { get; set; } = new();
    public Dictionary<string, List<ExchangeCandle>> Materials { get; set; } = new();
    public Dictionary<string, decimal> MaterialBase { get; set; } = new();

    public void SampleMaterials(ExchangeMaterialSample[] samples, long now)
    {
        foreach (var sample in samples)
        {
            if (sample.BasePrice <= 0 || sample.Volume < 0) continue;
            MaterialBase[sample.Id] = sample.BasePrice;
            if (!Materials.TryGetValue(sample.Id, out var history)) Materials[sample.Id] = history = new();
            var previous = history.LastOrDefault()?.Price ?? sample.BasePrice;
            // A bounded index anchored to the evaluator; one exceptional sale cannot reprice all raw materials.
            var target = sample.SoldPrice is { } observed ? Math.Clamp(observed, sample.BasePrice * .5m, sample.BasePrice * 2) : sample.BasePrice;
            var weight = sample.ConsumptionIndex && sample.Id == "AntimatterIndex" ? .1m : sample.Volume > 0 ? Math.Min(.1m, sample.Volume / (sample.Volume + 100000m)) : .01m;
            var value = previous + (target - previous) * weight;
            var bucket = now - now % 60000;
            if (history.LastOrDefault() is { } last && last.At == bucket)
                history[^1] = new(bucket, value, checked(last.Volume + sample.Volume));
            else if (history.Count == 0 || bucket > history[^1].At)
                history.Add(new(bucket, value, sample.Volume));
            Compact(history, now);
        }
        foreach (var group in new Dictionary<string, string[]>
        {
            ["ConstructionIndex"] = ["Steel", "Glass", "Plasteel", "Plastic"],
            ["IndustryIndex"] = ["Copper", "Gold", "Silver", "Uranium", "Plasma"],
            ["TextileIndex"] = ["Cloth", "Durathread"],
        })
        {
            var values = group.Value.Where(id => MaterialBase.GetValueOrDefault(id) > 0 && Materials.GetValueOrDefault(id)?.Count > 0)
                .Select(id => Materials[id][^1].Price / MaterialBase[id] * 100).ToArray();
            if (values.Length == 0) continue;
            MaterialBase[group.Key] = 100;
            if (!Materials.TryGetValue(group.Key, out var history)) Materials[group.Key] = history = new();
            var at = now - now % 60000;
            if (history.LastOrDefault()?.At == at) history[^1] = new(at, values.Average(), 0);
            else if (history.Count == 0 || at > history[^1].At) history.Add(new(at, values.Average(), 0));
            Compact(history, now);
        }
    }

    public static ExchangeMarket Create(long now)
    {
        var market = new ExchangeMarket();
        foreach (var asset in Assets)
        {
            market.Pools[asset.Id] = new() { MintAt = now };
            market.Sample(asset.Id, now, 0);
        }
        return market;
    }

    public decimal Spot(string asset)
    {
        var spec = Assets.Single(a => a.Id == asset);
        var pool = Pools[asset];
        // Virtual cash anchors the initial ask, but is NEVER available for withdrawals.
        return ((decimal)spec.Anchor * 10000 + pool.Cash) * UnitsPerCoin / pool.Tokens;
    }

    public ExchangeQuote Quote(string asset, long units)
    {
        if (units is < 1 or > MaxOrderUnits || !Pools.TryGetValue(asset, out var pool))
            throw new ArgumentException("invalid_order");
        var spec = Assets.Single(a => a.Id == asset);
        var quoteCash = (decimal)spec.Anchor * 10000 + pool.Cash;
        var buyGross = units < pool.Tokens ? checked((long)decimal.Ceiling(quoteCash * units / (pool.Tokens - units))) : 0;
        var buyFee = buyGross > 0 ? Math.Max(1, checked((long)decimal.Ceiling(buyGross * .01m))) : 0;
        var sellGross = checked((long)decimal.Floor(quoteCash * units / (pool.Tokens + units)));
        var sellFee = sellGross > 0 ? Math.Max(1, checked((long)decimal.Ceiling(sellGross * .01m))) : 0;
        return new(units, checked(buyGross + buyFee), Math.Max(0, sellGross - sellFee), buyFee, sellFee,
            sellGross <= pool.Cash && sellGross > sellFee);
    }

    public (long BankChange, long Fee) Trade(string asset, long units, bool buy, long limit, long wallet, long bank, long now)
    {
        var quote = Quote(asset, units);
        var pool = Pools[asset];
        if (buy)
        {
            if (quote.BuyCost <= 0 || quote.BuyCost > limit) throw new InvalidOperationException("price_changed");
            if (bank < quote.BuyCost) throw new InvalidOperationException("insufficient_spessos");
            pool.Tokens = checked(pool.Tokens - units);
            pool.Cash = checked(pool.Cash + quote.BuyCost - quote.BuyFee);
            pool.FeesBurned = checked(pool.FeesBurned + quote.BuyFee);
            Sample(asset, now, units);
            return (-quote.BuyCost, quote.BuyFee);
        }
        if (wallet < units) throw new InvalidOperationException("insufficient_crypto");
        if (!quote.CanSell) throw new InvalidOperationException("insufficient_liquidity");
        if (quote.SellProceeds < limit) throw new InvalidOperationException("price_changed");
        _ = checked(bank + quote.SellProceeds);
        pool.Tokens = checked(pool.Tokens + units);
        pool.Cash = checked(pool.Cash - quote.SellProceeds - quote.SellFee);
        pool.FeesBurned = checked(pool.FeesBurned + quote.SellFee);
        Sample(asset, now, units);
        return (quote.SellProceeds, quote.SellFee);
    }

    public string[] Mint(int requestedCoins, long now)
    {
        if (requestedCoins is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(requestedCoins));
        foreach (var pool in Pools.Values)
        {
            var elapsed = Math.Clamp(now - pool.MintAt, 0, 7200000L);
            pool.MintAllowance = Math.Min(MintUnitsPerHour * 2, pool.MintAllowance + elapsed * (decimal)MintUnitsPerHour / 3600000m);
            pool.MintAt = Math.Max(now, pool.MintAt);
        }
        var minted = new List<string>();
        for (var i = 0; i < requestedCoins; i++)
        {
            var asset = Assets.OrderByDescending(a => Pools[a.Id].MintAllowance).First();
            var pool = Pools[asset.Id];
            if (pool.MintAllowance < UnitsPerCoin) break;
            pool.MintAllowance -= UnitsPerCoin;
            pool.MinedUnits = checked(pool.MinedUnits + UnitsPerCoin);
            minted.Add(asset.CoinPrototype);
        }
        return minted.ToArray();
    }

    public void Sample(string asset, long now, long volume)
    {
        var pool = Pools[asset];
        var bucket = now - now % 60000;
        var last = pool.History.LastOrDefault();
        if (last != null && last.At == bucket)
            pool.History[^1] = new(bucket, Spot(asset), checked(last.Volume + volume));
        else if (last == null || bucket > last.At)
            pool.History.Add(new(bucket, Spot(asset), volume));
        // Up to seven days of real observations. No backfilled random candles.
        Compact(pool.History, now);
    }

    private static void Compact(List<ExchangeCandle> history, long now)
    {
        var older = history.Where(p => p.At >= now - 604800000L && p.At < now - 21600000L)
            .GroupBy(p => p.At / 900000L).Select(g => new ExchangeCandle(g.Last().At, g.Last().Price, g.Sum(p => p.Volume))).ToArray();
        var recent = history.Where(p => p.At >= now - 21600000L).ToArray();
        history.Clear();
        history.AddRange(older);
        history.AddRange(recent);
    }
}
