using System.Linq;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.Salvage.Expeditions;
namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionSystem
{
    private string PlanetBlockReason(EntityUid ship, Trip? trip, SectorPlanetView planet)
    {
        if (!planet.Landable) return "На газовый гигант нельзя высадиться";
        if (trip is { Phase: Phase.Planet }) return "Шаттл уже находится на планете";
        if (trip is { Phase: Phase.GeneratingPlanet }) return "Подготовка планеты ещё не завершена";
        if (trip != null && trip.Phase != Phase.Orbit) return "Дождитесь окончания перелёта";
        if (trip != null && !ResearchLandingAllowed(trip, planet.Index)) return "Нет принятого заказа для этой планеты";
        if (trip != null && !trip.LocalPlanet)
        {
            if (_encounterBusy.Contains(trip.Sector)) return "Определение обстановки в секторе";
            if (_orbits.TryGetValue(trip.Sector, out var orbit) && orbit.Pirates.Any(p => !p.Counted))
                return "Посадка недоступна, пока сектор контролируют пиратские корабли";
        }
        try { ValidateShip(ship); }
        catch (InvalidOperationException error) { return error.Message; }
        if (_salvage.GridExpeditionSettings(ship) is { Enabled: false }) return "Экспедиции на этом шаттле отключены";
        if (trip != null && _planetLaunching.Contains((PlanetSessionKey(trip), planet.Index)))
            return "Подготовка планеты другим экипажем ещё не завершена";
        if (trip != null && _planetMaps.TryGetValue((PlanetSessionKey(trip), planet.Index), out var map) && !CanJoinPlanet(map))
            return "Экспедиция на этой планете завершается: присоединиться уже нельзя";
        if (planet.Occupied && !planet.CanLand) return "Планета занята другим исследовательским рейсом";
        var range = _config.GetCVar(SectorExpeditionCVars.LandRange);
        if (planet.Distance > range) return $"Подлетите ближе к планете: посадка доступна с расстояния до {range:0} м";
        return planet.CanLand ? "" : "Посадка пока недоступна";
    }
}
