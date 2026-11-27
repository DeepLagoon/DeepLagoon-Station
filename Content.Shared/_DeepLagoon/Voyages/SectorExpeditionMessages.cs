using Robust.Shared.Configuration;
using Robust.Shared.Serialization;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Salvage;
namespace Content.Shared._DeepLagoon.Voyages;

[Serializable, NetSerializable]
public enum SectorExpeditionAction : byte
{ Refresh, Depart, Land, LeavePlanet, ReturnHome, PostResearch, CancelResearch, PublishCrew, WithdrawCrew, HireCrew, AcceptHire, DeclineHire, CancelHire, FinishHire, ConfirmHire, AcceptResearch, AbandonResearch }
[Serializable, NetSerializable]
public sealed class SectorExpeditionRequest : EntityEventArgs
{
    public NetEntity Console;
    public SectorExpeditionAction Action;
    public Guid Operation = Guid.NewGuid(), Order, Offer;
    public string Sector = "", Text = "", Title = "";
    public long Price;
    public int Planet = -1;
}
[Serializable, NetSerializable]
public sealed record SectorPlanetView(int Index, string Name, string Type, float X, float Y, int Difficulty, bool Completed, bool Occupied, float Distance, bool CanLand, bool Landable = true, string LandingBlockReason = "");
[Serializable, NetSerializable]
public sealed record SectorResearchView(Guid Id, string Sector, string Buyer, long Total, long Remaining, bool Mine, int Completed, int Planets);
[Serializable, NetSerializable]
public sealed record SectorCrewServiceView(Guid Id, string Captain, string Ship, string Description, int Members, int Weapons, long Price, int Level, int Experience, bool Mine, bool Available, bool Listed = true);
[Serializable, NetSerializable]
public sealed record SectorCrewHireView(Guid Id, string Captain, string Employer, string Ship, long Price, string Sector, int Status, bool Provider, bool Buyer);
[Serializable, NetSerializable]
public sealed class SectorExpeditionSnapshot : EntityEventArgs
{
    public NetEntity Console;
    public string Status = "", CurrentSector = "", Selected = "", DepartureBlockReason = "", ClaimedSector = "";
    public Guid ActiveOrder;
    public VoyageSnapshot Map = new();
    public SectorPlanetView[] Planets = [];
    public SectorResearchView[] Orders = [];
    public SectorCrewServiceView[] Crews = [];
    public SectorCrewHireView[] Hires = [];
    public Dictionary<string, int> Progress = new();
    public bool Home = true, InFlight, OnPlanet, CanDepart, CanReturn, WithoutFtlDrive, FocusSelected;
    public float FlightSeconds, ShipX, ShipY;
    public long SuggestedPool;
    public int MissionDone, MissionTotal, ActivePlanet = -1;
}

/// <summary>Small deterministic metadata only. Terrain is created exclusively by the native expedition job.</summary>
public static class SectorResearchDefinition
{
    public static bool Settled(string sector) => sector is VoyageRouteModel.Home or "centcomm" or "helios" or "nereid" or "forge" or "boreas" || sector.StartsWith("trade-", StringComparison.Ordinal);
    public static int Count(string sector, int seed) => 3 + (int)(VoyageGalaxy.Hash(sector + "/planets", seed) % 4);
    public static int PlanetSeed(string sector, int planet, int seed) => (int)(VoyageGalaxy.Hash(sector + "/planet/" + planet, seed) & 0x7fffffffu);
    public static int Difficulty(string sector, int planet, int seed) => 2 + (int)(VoyageGalaxy.Hash(sector + "/difficulty/" + planet, seed) % 3);
    public static long Pool(string sector, int seed)
    {
        var price = 9000L;
        for (var i = 0; i < Count(sector, seed); i++) price += Difficulty(sector, i, seed) * 1500;
        return price;
    }
    public static SalvageMissionParams MissionParameters(string sector, int planet, int seed)
    {
        var missionSeed = PlanetSeed(sector, planet, seed);
        return new() { Index = (ushort)(planet + 1), Seed = missionSeed, Difficulty = (DifficultyRating)Difficulty(sector, planet, seed),
            MissionType = missionSeed % 2 == 0 ? SalvageMissionType.Elimination : SalvageMissionType.Destruction };
    }
    public static float FlightTime(int routeMinutes, float driveStartup, float driveTravel, bool hasDrive = true) =>
        Math.Clamp(3 + routeMinutes * .05f + Math.Clamp(driveStartup, 0, 20) * .3f + Math.Clamp(driveTravel, 1, 60) * .12f, 5, 20) * (hasDrive ? 1 : 4);
}

[CVarDefs]
public sealed class SectorExpeditionCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("sector_expeditions.enabled", false, CVar.SERVER | CVar.REPLICATED);
    public static readonly CVarDef<int> MaxLoaded = CVarDef.Create("sector_expeditions.max_loaded", 16, CVar.SERVERONLY);
    public static readonly CVarDef<float> LandRange = CVarDef.Create("sector_expeditions.land_range", 160f, CVar.SERVERONLY);
}
