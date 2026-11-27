using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Player;
namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionSystem
{
    private const float HomePlanetMapScale = .05f;
    private SectorPlanet[] HomePlanetDefinitions(EntityUid map)
    {
        if (!TryComp<StarSystemMapComponent>(map, out var navigation)) return [];
        if (navigation.Definition != null) return navigation.Definition.Planets.ToArray();
        if (navigation.System is not { } system || !_prototypes.TryIndex(system, out var prototype)) return [];
        return prototype.Planets.Select((p, i) =>
        {
            var position = new Vector2(MathF.Cos(p.Angle), MathF.Sin(p.Angle)) * p.Distance;
            return new SectorPlanet { Id = "home-" + i, Template = p.Planet, Name = _prototypes.Index(p.Planet).Name, X = position.X, Y = position.Y };
        }).ToArray();
    }
    private bool SolidPlanet(SectorPlanet planet) => _prototypes.TryIndex<PlanetTypePrototype>(planet.Template, out var type) && type.Shader is not ("GasGiant" or "IceGiant");
    private SectorPlanetView[] HomePlanetViews(EntityUid ship, Trip? trip, EntityUid home)
    {
        var definitions = HomePlanetDefinitions(home);
        return definitions.Select((p, i) =>
        {
            var distance = Transform(ship).MapUid == home ? Vector2.Distance(_transform.GetWorldPosition(ship), new Vector2(p.X, p.Y)) : 0;
            var occupied = _planetMaps.TryGetValue((HomePlanetKey(home), i), out var map);
            var canLand = (trip == null || trip.LocalPlanet && trip.Phase == Phase.Orbit) && SolidPlanet(p) &&
                !HasComp<FTLComponent>(ship) && !_planetLaunching.Contains((HomePlanetKey(home), i)) && (!occupied || CanJoinPlanet(map)) &&
                distance <= _config.GetCVar(SectorExpeditionCVars.LandRange);
            return new SectorPlanetView(i, p.Name, p.Template, p.X * HomePlanetMapScale, p.Y * HomePlanetMapScale,
                SectorResearchDefinition.Difficulty(VoyageRouteModel.Home, i, _voyages.WorldSnapshot.Seed), false, occupied, distance, canLand, SolidPlanet(p));
        }).ToArray();
    }
    private static string HomePlanetKey(EntityUid map) => VoyageRouteModel.Home + "/" + map;
    private string PlanetSessionKey(Trip trip) => trip.LocalPlanet ? HomePlanetKey(trip.HomeMap) : trip.Sector;
    private async Task LandHome(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, int planet)
    {
        if (_researchClaims.ContainsKey((session.UserId.UserId, account.Profile)) || _trips.Values.Any(t => t.User == session.UserId.UserId && t.Profile == account.Profile))
            throw new InvalidOperationException("Сначала завершите текущий рейс или откажитесь от заказа");
        ValidateShip(ship);
        var transform = Transform(ship); var map = transform.MapUid!.Value;
        if (HasComp<SectorExpeditionMapComponent>(map) || HasComp<Content.Server.Salvage.Expeditions.SalvageExpeditionComponent>(map))
            throw new InvalidOperationException("Бесплатная экспедиция доступна из родного сектора");
        var planets = HomePlanetDefinitions(map);
        if (planet < 0 || planet >= planets.Length || !SolidPlanet(planets[planet])) throw new InvalidOperationException("На газовый гигант нельзя высадиться");
        var target = planets[planet];
        if (Vector2.Distance(_transform.GetWorldPosition(ship), new Vector2(target.X, target.Y)) > _config.GetCVar(SectorExpeditionCVars.LandRange))
            throw new InvalidOperationException("Подлетите ближе к выбранной планете");
        var trip = new Trip { Id = Guid.NewGuid(), User = session.UserId.UserId, Profile = account.Profile, Slot = account.Slot,
            Cycle = _voyages.WorldSnapshot.Cycle, Ship = ship, Console = console, HomeMap = map, Home = new(map, _transform.GetWorldPosition(ship)),
            HomeAngle = _transform.GetWorldRotation(ship), Sector = VoyageRouteModel.Home, Name = Name(ship), LocalPlanet = true, Phase = Phase.Orbit };
        CaptureCrew(trip);
        if (trip.Bodies.Count == 0 && session.AttachedEntity is { } body) trip.Bodies.Add(body);
        _trips[ship] = trip; EnsureComp<SectorExpeditionShipComponent>(ship).Trip = trip.Id;
        try { await Land(session, console, ship, account, planet); }
        catch { _trips.Remove(ship); RemComp<SectorExpeditionShipComponent>(ship); throw; }
    }
}
