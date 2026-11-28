using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Shared.Player;
using Robust.Shared.Timing;
namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionSystem
{
    private sealed class ResearchClaim
    {
        public required Guid Order, Cycle, Vessel;
        public required string Sector;
        public HashSet<int> Planets = new();
        public bool Started, Returning;
    }
    private readonly Dictionary<(Guid User, int Profile), ResearchClaim> _researchClaims = new();
    private async Task AcceptResearch(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, SectorExpeditionRequest request)
    {
        if (_researchClaims.TryGetValue((session.UserId.UserId, account.Profile), out var current))
        {
            if (current.Order == request.Order && current.Vessel == VesselIdentity(ship) && !current.Started)
            {
                await StartAcceptedResearch(session, console, ship, account, request, current);
                return;
            }
            throw new InvalidOperationException("Сначала завершите текущий заказ и вернитесь домой либо откажитесь от него");
        }
        if (_trips.ContainsKey(ship)) throw new InvalidOperationException("Новый заказ можно взять после возвращения домой");
        if (SectorResearchDefinition.Settled(request.Sector)) throw new InvalidOperationException("Технический сектор используется только для транзита");
        if (!VoyageVisibility.VisibleSystems(_voyages.RouteGraph).Contains(request.Sector))
            throw new InvalidOperationException("Сначала исследуйте маршруты к этому сектору");
        if ((HasComp<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(ship) || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(ship)) && !OwnsShip(session, ship))
            throw new InvalidOperationException("Взять заказ для шаттла может его владелец");
        var board = await _db.ReadSectorResearchAsync(_voyages.WorldSnapshot.Cycle, session.UserId.UserId, account.Profile);
        var order = board.Orders.FirstOrDefault(o => o.Id == request.Order && o.Sector == request.Sector)
            ?? throw new InvalidOperationException("Заявка уже закрыта или относится к другому сектору");
        if (!CanUse(session, console) || !_voyages.Ready) return;
        if (_voyages.RouteGraph.SurveyPath(order.Sector).Length < 2 && _voyages.RouteGraph.Path(VoyageRouteModel.Home, order.Sector).Length < 2)
            throw new InvalidOperationException("К сектору нет доступного маршрута");
        var completed = _voyages.WorldSnapshot.Research.GetValueOrDefault(order.Sector)?.Completed;
        var claim = new ResearchClaim { Order = order.Id, Cycle = order.Cycle, Sector = order.Sector, Vessel = VesselIdentity(ship),
            Planets = Enumerable.Range(0, order.Planets).Where(p => completed?.Contains(p) != true).ToHashSet() };
        if (claim.Planets.Count == 0) throw new InvalidOperationException("Нет доступных заданий на исследование этого сектора");
        _researchClaims[(session.UserId.UserId, account.Profile)] = claim;
        await StartAcceptedResearch(session, console, ship, account, request, claim);
    }
    private async Task StartAcceptedResearch(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account,
        SectorExpeditionRequest request, ResearchClaim claim)
    {
        try { await Depart(session, console, ship, account, request); }
        finally
        {
            // Failed validation never leaves a stranded accepted order requiring a second button.
            if (!claim.Started) _researchClaims.Remove((session.UserId.UserId, account.Profile));
        }
    }
    private bool ResearchLandingAllowed(Trip trip, int planet) => trip.LocalPlanet ||
        _researchClaims.TryGetValue((trip.User, trip.Profile), out var claim) && claim.Started && !claim.Returning &&
        claim.Cycle == trip.Cycle && claim.Sector == trip.Sector && claim.Vessel == VesselIdentity(trip.Ship) && claim.Planets.Contains(planet);

    private void AbandonResearch(ICommonSession session, EconomyAccount account)
    {
        var key = (session.UserId.UserId, account.Profile);
        if (!_researchClaims.TryGetValue(key, out var claim)) return;
        var trip = _trips.Values.FirstOrDefault(t => t.User == key.Item1 && t.Profile == key.Item2);
        if (trip == null) { _researchClaims.Remove(key); return; }
        claim.Returning = true;
        trip.HomeAfterPlanet = true;
        _emergencyPending.Add(trip.Ship);
    }
    public (string Sector, Guid Flight) PlayerLocation(EntityUid? actor)
    {
        if (actor == null || !Exists(actor.Value)) return (VoyageRouteModel.Home, Guid.Empty);
        var ship = Transform(actor.Value).GridUid;
        if (ship != null && _trips.TryGetValue(ship.Value, out var trip))
            return !trip.LocalPlanet && trip.Phase is (Phase.Departing or Phase.ReturningHome or Phase.EmergencyHome) ? ("", trip.Id) : (trip.Sector, Guid.Empty);
        return Transform(actor.Value).MapUid is { } map && TryComp<SectorExpeditionMapComponent>(map, out var orbit) ? (orbit.Sector, Guid.Empty) : (VoyageRouteModel.Home, Guid.Empty);
    }
}
