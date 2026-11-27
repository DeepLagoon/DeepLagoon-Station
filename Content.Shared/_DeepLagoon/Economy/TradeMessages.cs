using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Economy;

[Serializable, NetSerializable]
public enum TradeAction : byte { Refresh, Sell, BuyOrder, Deal, Cancel, Claim, QuoteBasket, PurchaseBasket, Comment }
[Serializable, NetSerializable]
public enum TradeFilter : byte { All, Sale, Buy, Mine }
[Serializable, NetSerializable]
public enum TradeSort : byte { Newest, Cheapest, MostExpensive }

[Serializable, NetSerializable]
public sealed class TradeRequest : EntityEventArgs
{
    public NetEntity Console;
    public TradeAction Action;
    public Guid Token, Offer;
    public NetEntity? Item;
    public string Prototype = "", Description = "", Search = "";
    public int Quantity = 1, Condition = 100, Charge, StackUnits = 1, Page;
    public long Price;
    public TradeFilter Filter;
    public TradeSort Sort;
    public string Category = "";
    public string Group = "";
    public int SellerPage;
    public Guid Quote;
    public TradeBasketSelection[] Lines = [];
}

[Serializable, NetSerializable]
public sealed record TradeOfferView(Guid Id, bool Mine, bool BuyOrder, string Character, string Name, string Description,
    long Price, int Remaining, int Condition, int Charge, int StackUnits, long Expires)
{
    public string Prototype { get; init; } = "";
    public string Category { get; init; } = "other";
    public bool? HasBattery { get; init; }
    public bool Splittable { get; init; }
    public long OriginalPrice { get; init; }
    public int OriginalUnits { get; init; }
}
[Serializable, NetSerializable]
public sealed record TradeBasketSelection(Guid Offer, int Units);
[Serializable, NetSerializable]
public sealed record TradeGroupView(string Key, string Prototype, string Name, bool BuyOrder, long Price, int PriceUnits);
[Serializable, NetSerializable]
public sealed record TradeQuoteLineView(Guid Offer, string Prototype, string Name, string Character, int Units, long Price);
[Serializable, NetSerializable]
public sealed record TradeQuoteView(Guid Id, long Total, TradeQuoteLineView[] Lines);
[Serializable, NetSerializable]
public sealed record TradeItemView(NetEntity Entity, string Name, string Prototype, int Condition, int Charge, int StackUnits)
{
    public bool? HasBattery { get; init; }
}
[Serializable, NetSerializable]
public sealed record TradeDeliveryView(Guid Id, string Name)
{
    public string Prototype { get; init; } = "";
}

[Serializable, NetSerializable]
public sealed class TradeSnapshot : EntityEventArgs
{
    public bool ReadOnly, CanComment;
    public Guid CommentOffer;
    public string CommentAuthor = "";
    public TradeCommentView[] Comments = [];
    public NetEntity Console;
    public Guid Token;
    public bool Enabled;
    public string Status = "";
    public long Bank;
    public int Page, Pages;
    public int Total;
    public TradeOfferView[] Offers = [];
    public TradeItemView[] Items = [];
    public TradeDeliveryView[] Deliveries = [];
    public TradeGroupView[] Groups = [];
    public string Group = "";
    public int SellerPage, SellerPages;
    public TradeQuoteView? Quote;
    public bool BasketPurchased;
}

[Serializable, NetSerializable]
public sealed record TradeCommentView(string Author, string Text, long Time);
