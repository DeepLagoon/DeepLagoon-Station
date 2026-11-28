using System.Linq;
using System.Numerics;

namespace Content.Shared._DeepLagoon.Voyages;

// Presentation fixture only. It never creates entities, money, real orders or server requests.
public sealed record VoyageOrder(string Id, string Prototype, string Name, int Quantity, int Reward, string Buyer);
public sealed record VoyageDestination(string Id, string Name, Vector2 Position, string PlanetType, string Role, bool Known,
    VoyageOrder[] Orders, string MarketCategory = "", string[]? Goods = null, bool Researched = false);
public sealed record VoyageLane(string From, string To, int Minutes, bool Charted = true, bool Dangerous = false);
public sealed record VoyageFlight(string Name, string[] Stops, double Departure, double Seconds, bool Survey = false, int? CrewId = null, bool Mine = false)
{
    public bool IsLocal => Mine || CrewId != null;
    public double Progress(double now) => Math.Clamp((now - Departure) / Seconds, 0, 1);
}

public sealed record VoyageCrewOffer(string Id, string Name, int Members, int Cost, double Speed, double SurveySpeed);
public sealed record VoyageCrew(int Id, VoyageCrewOffer Offer, int Level = 1, int Experience = 0);

public class VoyageRouteModel
{
    public int Seed { get; private set; }
    public Guid Cycle { get; private set; }
    public VoyageRouteModel() => VoyageGalaxy.Extend(Systems, Lanes, 0);
    public void SetGalaxy(int seed, Guid cycle)
    {
        if (Seed == seed && Cycle == cycle) return;
        Seed = seed; Cycle = cycle;
        Systems.RemoveAll(s => s.Id.StartsWith("sector-", StringComparison.Ordinal) || s.Id.StartsWith("trade-", StringComparison.Ordinal));
        Lanes.RemoveAll(l => l.From.StartsWith("sector-", StringComparison.Ordinal) || l.To.StartsWith("sector-", StringComparison.Ordinal) || l.From.StartsWith("trade-", StringComparison.Ordinal) || l.To.StartsWith("trade-", StringComparison.Ordinal) || l.From == "centcomm" || l.To == "centcomm");
        for (var i = 0; i < Systems.Count; i++) Systems[i] = Systems[i] with { Known = Systems[i].Id is Home or "helios" or "nereid" or "forge" or "boreas" or "centcomm" };
        for (var i = 0; i < Lanes.Count; i++) Lanes[i] = Lanes[i] with { Charted = System(Lanes[i].From).Known && System(Lanes[i].To).Known };
        VoyageGalaxy.Extend(Systems, Lanes, seed); _snapshotKey = ""; Revision++;
    }
    public const string Home = "colossus";
    public List<VoyageDestination> Systems { get; } =
    new()
    {
        new("centcomm", "Центком", new(0, -1570), "PlanetTypeHabitable", "Центральное командование", true, []),
        new(Home, "Колосс-Централ", new(0, -120), "PlanetTypeHabitable", "Торговый узел", true,
            [new("C-101", "FoodPizzaMargherita", "Пицца маргарита", 48, 72000, "Снабжение Колосса"),
             new("C-102", "Brutepack", "Перевязочные материалы", 24, 18000, "Госпиталь")]),
        new("helios", "Гелиос", new(-760, -390), "PlanetTypeDesert", "Аграрные колонии", true,
            [new("H-201", "SheetSteel", "Сталь", 600, 45000, "Аграрный союз"),
             new("H-202", "SheetGlass", "Стекло", 300, 32000, "Купольные фермы")]),
        new("nereid", "Нереида", new(530, -420), "PlanetTypeHabitable", "Медицинский сектор", true,
            [new("N-301", "Brutepack", "Перевязочные материалы", 60, 54000, "Клиники Нереиды"),
             new("N-302", "FoodPizzaMargherita", "Пицца маргарита", 36, 63000, "Служба снабжения")]),
        new("forge", "Гефест", new(-330, -970), "PlanetTypeVolcanic", "Промышленный пояс", true,
            [new("F-401", "SheetPlasma", "Твёрдая плазма", 200, 84000, "Литейный синдикат"),
             new("F-402", "SheetGlass", "Стекло", 400, 46000, "Индустриальный комплекс")]),
        new("boreas", "Борея", new(1180, -860), "PlanetTypeIce", "Исследовательские станции", true,
            [new("B-501", "RogueSiliconResearchDisk50000", "Диск исследовательских очков", 4, 96000, "Полярный институт"),
             new("B-502", "FoodPizzaMargherita", "Пицца маргарита", 24, 60000, "Экспедиционный корпус")]),
        new("echo", "Эхо-17", new(-430, 280), "PlanetTypeGasGiant", "Внешняя колония", false,
            [new("E-601", "SheetSteel", "Сталь", 900, 135000, "Колониальная администрация"),
             new("E-602", "Brutepack", "Перевязочные материалы", 100, 110000, "Колониальная администрация")]),
        new("frontier", "Рубеж-9", new(440, 300), "PlanetTypeBarren", "Дальний сектор", false,
            [new("R-701", "SheetPlasma", "Твёрдая плазма", 400, 180000, "Снабжение Рубежа")]),
    };
    public List<VoyageLane> Lanes { get; } =
    new()
    {
        new(Home, "helios", 12), new(Home, "nereid", 16), new(Home, "forge", 14), new(Home, "boreas", 18),
        new("forge", "boreas", 21, Dangerous: true), new("helios", "nereid", 25),
        new("helios", "echo", 22, false), new("nereid", "frontier", 30, false),
        new("echo", "forge", 26, false, true),
    };
    public List<VoyageFlight> Flights { get; } =
    new()
    {
        new("Меридиан-04", [Home, "helios"], -300, 1500),
        new("Медицинский экспресс", ["nereid", Home], -850, 1900),
        new("Индустриальный конвой", ["forge", "boreas"], -1150, 2400),
    };
    public static readonly VoyageCrewOffer[] CrewOffers =
    [
        new("cargo", "Транспортный экипаж", 4, 6000, 1, 1),
        new("survey", "Разведывательный экипаж", 3, 9000, 1, 1.5),
        new("express", "Курьерский экипаж", 5, 15000, 1.3, 1.3),
        new("mercenary", "Наёмнический отряд", 8, 25000, 1, 1),
    ];
    public List<VoyageCrew> Crews { get; } = new();
    public int HiringBudget { get; private set; } = 150000;
    public bool IsBusy(VoyageCrew crew) => Live ? CrewIdentifiers.TryGetValue(crew.Id, out var key) && ServerFlights.Any(f => f.Mine && f.Crew == key && !f.Paid) : Flights.Any(f => f.CrewId == crew.Id && f.Progress(Clock) < 1);
    public VoyageCrew? Hire(string offerId)
    {
        var offer = CrewOffers.FirstOrDefault(o => o.Id == offerId);
        if (offer == null || HiringBudget < offer.Cost) return null;
        HiringBudget -= offer.Cost;
        var crew = new VoyageCrew(Crews.Count + 1, offer);
        Crews.Add(crew); Revision++; return crew;
    }
    public int TravelSeconds(string[] path, VoyageCrew crew, bool survey) =>
        (int)Math.Ceiling(Minutes(path) * 60 / ((survey ? crew.Offer.SurveySpeed : crew.Offer.Speed) * (1 + .04 * (crew.Level - 1))));
    public bool Live { get; private set; }
    public double TimeScale { get; private set; } = 1;
    public long BankBalance { get; private set; }
    public string Origin { get; private set; } = "";
    public Dictionary<int, Guid> CrewIdentifiers { get; } = new();
    public VoyageHireView[] HireOffers { get; private set; } = [];
    public VoyageProductView[] Products { get; private set; } = [];
    public Dictionary<string, double> Piracy { get; private set; } = new();
    public string PlayerSector { get; private set; } = Home;
    public Guid PlayerFlight { get; private set; }
    public double SelectedRisk { get; private set; }
    public VoyagePurchaseQuote? Quote { get; private set; }
    public VoyageCartQuoteView? CartQuote { get; private set; }
    public Guid CartCommitted { get; private set; }
    public VoyageFlightView[] ServerFlights { get; private set; } = [];
    public Dictionary<string, string> Manifests { get; } = new();
    public Dictionary<string, long> Budgets { get; private set; } = new();
    private string _snapshotKey = "";
    public void UseServer()
    {
        Live = true; Speed = 1; Flights.Clear(); Crews.Clear();
        for (var i = 0; i < Systems.Count; i++) Systems[i] = Systems[i] with { Orders = [] };
        Revision++;
    }
    public void Apply(VoyageSnapshot snapshot)
    {
        if (!Live) UseServer();
        SetGalaxy(snapshot.Seed, snapshot.Cycle);
        HireOffers = snapshot.HireOffers; Products = snapshot.Products; Piracy = snapshot.Piracy; SelectedRisk = snapshot.Risk; Quote = snapshot.Quote;
        CartQuote = snapshot.CartQuote; CartCommitted = snapshot.CartCommitted;
        PlayerSector = snapshot.PlayerSector; PlayerFlight = snapshot.PlayerFlight;
        Clock = snapshot.Clock; TimeScale = snapshot.TimeScale; BankBalance = snapshot.Bank; Origin = snapshot.Origin; Budgets = snapshot.Budgets;
        var key = string.Join("|", snapshot.Orders.Select(o => o.Id)) + ";" + string.Join("|", snapshot.Crews.Select(c => c.Id + ":" + c.Level + ":" + c.Experience)) +
            ";" + string.Join("|", snapshot.Flights.Select(f => f.Id + ":" + f.Delivered + ":" + f.Paid)) + ";" + snapshot.Bank + ";" + string.Join(",", snapshot.Known) + ";" + string.Join(",", snapshot.HireOffers.Select(o => o.Id)) + ";" + snapshot.Selected + ";" + string.Join(",", snapshot.Products.Select(p => p.Id + ":" + p.Price)) + ";" + snapshot.Quote?.Id + ";" + string.Join(",", snapshot.Flights.Select(f => f.Failed + ":" + f.CargoIssued + ":" + f.Combat));
        key += ";cart:" + snapshot.CartQuote?.Id + ";paid:" + snapshot.CartCommitted;
        if (key == _snapshotKey) return;
        _snapshotKey = key;
        Manifests.Clear();
        foreach (var order in snapshot.Orders) Manifests[order.Id] = order.Manifest;
        for (var i = 0; i < Systems.Count; i++)
        {
            var node = Systems[i];
            Systems[i] = node with { Name = node.Id == Home ? snapshot.Origin : node.Name, Known = node.Known || snapshot.Known.Contains(node.Id), Orders = snapshot.Orders.Where(o => o.Destination == node.Id)
                .Select(o => new VoyageOrder(o.Id, o.Prototype, o.Manifest, o.Quantity, o.Reward, o.Buyer)).ToArray() };
        }
        for (var i = 0; i < Lanes.Count; i++) if (System(Lanes[i].From).Known && System(Lanes[i].To).Known) Lanes[i] = Lanes[i] with { Charted = true };
        CrewIdentifiers.Clear(); Crews.Clear();
        foreach (var crew in snapshot.Crews)
        {
            var offer = CrewOffers.FirstOrDefault(o => o.Id == crew.Offer);
            if (offer == null) continue;
            CrewIdentifiers[crew.Number] = crew.Id; Crews.Add(new(crew.Number, offer, crew.Level, crew.Experience));
        }
        ServerFlights = snapshot.Flights; Flights.Clear();
        foreach (var flight in snapshot.Flights)
        {
            var returning = flight.Delivered || Clock >= flight.Arrive;
            var stops = returning ? flight.Stops.Reverse().ToArray() : flight.Stops;
            int? crew = flight.Mine ? CrewIdentifiers.Where(c => c.Value == flight.Crew).Select(c => (int?)c.Key).FirstOrDefault() : null;
            Flights.Add(new(flight.Name, stops, flight.Failed ? Clock - 1 : returning ? flight.Arrive : flight.Depart,
                flight.Failed ? 1 : returning ? flight.Return - flight.Arrive : flight.Arrive - flight.Depart, flight.Survey, crew, flight.Mine));
        }
        Revision++;
    }
    public double Clock { get; private set; }
    public bool Paused;
    public int Speed = 8;
    public int Revision { get; private set; }
    private int _serial = 4;
    public VoyageDestination System(string id) => Systems.Single(s => s.Id == id);

    public string[] Path(string start, string destination)
    {
        if (!System(start).Known || !System(destination).Known) return [];
        var costs = Systems.ToDictionary(s => s.Id, _ => int.MaxValue);
        var previous = new Dictionary<string, string>();
        var visited = new HashSet<string>(); costs[start] = 0;
        while (true)
        {
            var current = Systems.Where(s => s.Known && !visited.Contains(s.Id) && costs[s.Id] < int.MaxValue)
                .OrderBy(s => costs[s.Id]).FirstOrDefault();
            if (current == null) return [];
            if (current.Id == destination) break;
            visited.Add(current.Id);
            foreach (var lane in Lanes.Where(l => l.Charted && (l.From == current.Id || l.To == current.Id)))
            {
                var next = lane.From == current.Id ? lane.To : lane.From;
                var cost = costs[current.Id] + lane.Minutes;
                if (!System(next).Known || cost >= costs[next]) continue;
                costs[next] = cost; previous[next] = current.Id;
            }
        }
        var path = new List<string> { destination };
        while (path[^1] != start) path.Add(previous[path[^1]]);
        path.Reverse(); return path.ToArray();
    }
    public string[] SurveyPath(string destination)
    {
        if (System(destination).Known) return [];
        var routes = Lanes.Where(l => !l.Charted && (l.From == destination || l.To == destination))
            .Select(l => l.From == destination ? l.To : l.From).Where(id => System(id).Known)
            .Select(id => Path(Home, id)).Where(path => path.Length > 0)
            .Select(path => (string[])[..path, destination]).ToArray();
        return routes.OrderBy(Minutes).FirstOrDefault() ?? [];
    }
    public int Minutes(string[] path) => Enumerable.Range(1, Math.Max(0, path.Length - 1))
        .Sum(i => Lanes.Single(l => l.From == path[i - 1] && l.To == path[i] || l.To == path[i - 1] && l.From == path[i]).Minutes);

    public bool Dispatch(string destination, bool survey, int crewId)
    {
        var crew = Crews.FirstOrDefault(c => c.Id == crewId);
        if (crew == null || IsBusy(crew)) return false;
        if (destination == Home || Flights.Count(f => f.Progress(Clock) < 1) >= 8) return false;
        var path = Path(Home, destination);
        if (survey)
        {
            var lane = Lanes.FirstOrDefault(l => !l.Charted && (l.From == destination || l.To == destination) &&
                System(l.From == destination ? l.To : l.From).Known);
            if (lane == null) return false;
            var approach = Path(Home, lane.From == destination ? lane.To : lane.From);
            if (approach.Length == 0) return false;
            path = [..approach, destination];
        }
        if (path.Length < 2) return false;
        Flights.Add(new((survey ? "Разведка-" : "Рейс-") + _serial++, path, Clock, TravelSeconds(path, crew, survey), survey, crewId));
        Revision++; return true;
    }
    public void Advance(double seconds)
    {
        if (Live) { if (double.IsFinite(seconds) && seconds > 0) Clock += Math.Min(seconds, .25); return; }
        if (Paused || !double.IsFinite(seconds) || seconds <= 0) return;
        Clock += Math.Min(seconds, .25) * Speed;
        foreach (var flight in Flights.Where(f => f.Survey && f.Progress(Clock) >= 1).ToArray())
        {
            var destination = flight.Stops[^1];
            var index = Systems.FindIndex(s => s.Id == destination);
            if (Systems[index].Known) continue;
            Systems[index] = Systems[index] with { Known = true };
            for (var i = 0; i < Lanes.Count; i++)
                if (Systems.Any(s => s.Id == Lanes[i].From && s.Known) && Systems.Any(s => s.Id == Lanes[i].To && s.Known))
                    Lanes[i] = Lanes[i] with { Charted = true };
            Revision++;
        }
        if (Flights.Count > 16) Flights.RemoveAll(f => f.Progress(Clock) >= 1 && !f.Survey);
    }
    public Vector2 Direction(VoyageFlight flight)
    {
        var progress = flight.Progress(Clock) * Minutes(flight.Stops);
        for (var i = 1; i < flight.Stops.Length; i++)
        {
            var duration = Minutes([flight.Stops[i - 1], flight.Stops[i]]);
            if (progress <= duration) return System(flight.Stops[i]).Position - System(flight.Stops[i - 1]).Position;
            progress -= duration;
        }
        return System(flight.Stops[^1]).Position - System(flight.Stops[^2]).Position;
    }
    public Vector2 Position(VoyageFlight flight)
    {
        var progress = flight.Progress(Clock) * Minutes(flight.Stops);
        for (var i = 1; i < flight.Stops.Length; i++)
        {
            var duration = Minutes([flight.Stops[i - 1], flight.Stops[i]]);
            if (progress <= duration) return Vector2.Lerp(System(flight.Stops[i - 1]).Position, System(flight.Stops[i]).Position, (float)(progress / duration));
            progress -= duration;
        }
        return System(flight.Stops[^1]).Position;
    }
}
