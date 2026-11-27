using Robust.Shared.Serialization;
namespace Content.Shared._DeepLagoon.Voyages;

[Serializable, NetSerializable]
public enum VoyageAction : byte { Refresh, Hire, Dispatch, Patrol, QuotePurchase, Purchase, ClaimCargo, QuoteImportCart, QuoteDeliveryCart, ConfirmCart }
[Serializable, NetSerializable]
public sealed class VoyageRequest : EntityEventArgs
{
    public NetEntity Console;
    public VoyageAction Action;
    public Guid Operation, Crew, Quote;
    public int Quantity = 1;
    public string Product = "", Search = "";
    public string Offer = "", Order = "", Destination = "";
    public bool Survey;
    public VoyageCartLine[] Cart = [];
}
[Serializable, NetSerializable]
public sealed record VoyageCartLine(string Sector, string Product, string Order, int Quantity = 1);
[Serializable, NetSerializable]
public sealed record VoyageCartRouteView(string Sector, string Name, string Manifest, string Crew, int Level, double Seconds, double Risk,
    long PurchaseCost, long Reward, long GoodsValue);
[Serializable, NetSerializable]
public sealed record VoyageCartQuoteView(Guid Id, bool Imports, int HireCount, long HireCost, long PurchaseCost, long Reward,
    long GoodsValue, long EstimatedNet, VoyageCartRouteView[] Routes);
[Serializable, NetSerializable]
public sealed record VoyageOrderView(string Id, string Destination, string Name, string Prototype, int Quantity, int Reward, string Buyer, string Manifest);
[Serializable, NetSerializable]
public sealed record VoyageCrewView(Guid Id, string Offer, int Number, int Level = 1, int Experience = 0);
[Serializable, NetSerializable]
public sealed record VoyageHireView(string Id, string Offer, int Level, int Cost);
[Serializable, NetSerializable]
public sealed record VoyageProductView(string Id, string Prototype, string Name, long Price);
[Serializable, NetSerializable]
public sealed record VoyagePurchaseQuote(Guid Id, string Name, long Total, int Quantity, double Risk);
[Serializable, NetSerializable]
public sealed record VoyageFlightView(Guid Id, bool Mine, Guid Crew, string Name, string[] Stops, double Depart, double Arrive, double Return, bool Delivered, bool Paid, bool Survey, string Origin, bool Failed = false, int Kind = 0, int Combat = -1, bool CargoIssued = false, long Cost = 0);
[Serializable, NetSerializable]
public sealed class VoyageSnapshot : EntityEventArgs
{
    public NetEntity Console;
    public string Status = "", Origin = "";
    public Guid Cycle;
    public int Seed;
    public string Selected = "", PlayerSector = VoyageRouteModel.Home;
    public Guid PlayerFlight;
    public double Risk;
    public Dictionary<string, double> Piracy = new();
    public VoyageHireView[] HireOffers = [];
    public VoyageProductView[] Products = [];
    public VoyagePurchaseQuote? Quote;
    public VoyageCartQuoteView? CartQuote;
    public Guid CartCommitted, CartPending;
    public long Bank;
    public double Clock;
    public double TimeScale = 1;
    public VoyageOrderView[] Orders = [];
    public VoyageCrewView[] Crews = [];
    public VoyageFlightView[] Flights = [];
    public string[] Known = [];
    public Dictionary<string, long> Budgets = new();
}
