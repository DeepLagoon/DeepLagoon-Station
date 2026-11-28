using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Exchange;

[Serializable, NetSerializable]
public enum ExchangeAction : byte { Refresh, Buy, Sell, Deposit, PreviewTransfer, Transfer, Login, CreateWallet, Logout, ChangePassword }

[Serializable, NetSerializable]
public sealed class ExchangeRequest : EntityEventArgs
{
    public NetEntity Pda;
    public ExchangeAction Action;
    public string Asset;
    public long Units;
    public Guid Quote;
    public int PeriodHours;
    public string Recipient;
    public string Password;
    public string NewPassword;
    public ExchangeRequest(NetEntity pda, ExchangeAction action, string asset, long units, Guid quote = default, int periodHours = 24,
        string recipient = "", string password = "", string newPassword = "")
    { Pda = pda; Action = action; Asset = asset; Units = units; Quote = quote; PeriodHours = periodHours; Recipient = recipient; Password = password; NewPassword = newPassword; }
}

[Serializable, NetSerializable]
public sealed record ExchangePoint(long At, double Price, long Volume);

[Serializable, NetSerializable]
public sealed record ExchangeAssetView(string Id, string Symbol, double Price, long WalletUnits,
    long Liquidity, ExchangePoint[] History);

[Serializable, NetSerializable]
public sealed record ExchangeMaterialView(string Id, double BasePrice, double IndexPrice, ExchangePoint[] History);

[Serializable, NetSerializable]
public sealed class ExchangeSnapshot : EntityEventArgs
{
    public NetEntity Pda;
    public bool Enabled;
    public long At;
    public int PeriodHours = 24;
    public long Bank;
    public bool HasBank = true;
    public string Address = "";
    public bool WalletUnlocked;
    public bool NeedsPassword;
    public string WalletCharacter = "";
    public ExchangeWalletView[] Wallets = [];
    public ExchangeWithdrawalView[] Withdrawals = [];
    public Guid TransferQuote;
    public string RecipientAddress = "";
    public string RecipientCharacter = "";
    public long TransferUnits;
    public string TransferAsset = "";
    public string Status = "";
    public string SelectedAsset = "contrib";
    public long Units = 1000;
    public Guid Quote;
    public long BuyCost;
    public long SellProceeds;
    public bool CanSell;
    public ExchangeAssetView[] Assets = [];
    public ExchangeMaterialView[] Materials = [];
}

[Serializable, NetSerializable]
public sealed record ExchangeWalletView(string Address, string Name, bool NeedsPassword);

[Serializable, NetSerializable]
public sealed record ExchangeWithdrawalView(long At, long Amount, string AccountOwner, bool Cash, string Asset, long Units);
