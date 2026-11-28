using System;
using System.Collections.Generic;
namespace Content.Server.Database;

public sealed class SectorResearchProgress
{
    public Guid Encounter { get; set; }
    public long NextEncounter { get; set; }
    public int Pirates { get; set; }
    public HashSet<int> DefeatedPirates { get; set; } = new();
    public HashSet<int> Completed { get; set; } = new();
    public Dictionary<int, SectorPlanetLease> Leases { get; set; } = new();
}
public sealed record SectorPlanetLease(Guid Trip, Guid Run);
public sealed record SectorResearchOrder(Guid Id, Guid Cycle, string Sector, string Buyer, long Total, long Remaining,
    Guid User, int Profile, int Slot, byte Faction, int Planets, HashSet<int> Paid, bool Closed = false, bool Automatic = false, bool DebugFunded = false, bool DebugPirateTest = false);
public sealed record SectorCrewService(Guid Id, Guid User, int Profile, int Slot, string Captain, string Ship, string Description,
    int Members, int Weapons, long Price, int Level = 1, int Experience = 0, bool Listed = true, Guid Vessel = default);
public sealed record SectorCrewHire(Guid Id, Guid Offer, Guid Buyer, int BuyerProfile, int BuyerSlot, string Employer,
    Guid Provider, int ProviderProfile, int ProviderSlot, string Captain, string Ship, long Price, string Sector, int Status = 0);
// Hire statuses: pending, accepted, awaiting buyer confirmation, paid, declined/refunded, failed/refunded.
public sealed record SectorResearchBoard(SectorResearchOrder[] Orders, SectorCrewService[] Crews, SectorCrewHire[] Hires, Guid[] BusyOffers);

public sealed record SectorEncounterInfo(Guid Id, int[] Remaining);

public sealed record AutomaticSectorResearchTarget(string Sector, int Planets, long Reward);
public sealed record AutomaticSectorResearchRefresh(int Created, long NextRefresh);

public sealed record SectorResearchParticipant(Guid User, int Profile, Guid CrewOffer);
public sealed record SectorResearchPayment(Guid User, int Profile, long Amount);
