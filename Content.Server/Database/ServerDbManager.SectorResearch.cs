using System.Threading;
using System.Threading.Tasks;
namespace Content.Server.Database;
public partial interface IServerDbManager
{
    Task<AutomaticSectorResearchRefresh> RefreshAutomaticSectorResearchAsync(Guid cycle, AutomaticSectorResearchTarget[] targets, long now, long refreshInterval = 900000, CancellationToken cancel = default);
    Task<SectorEncounterInfo> SectorEncounterAsync(Guid cycle, string sector, string[] route, double heat, long now, Guid encounter, int defeated, CancellationToken cancel = default);
    Task DiscoverSectorAsync(Guid cycle, string sector, CancellationToken cancel = default);
    Task<SectorResearchBoard> ReadSectorResearchAsync(Guid cycle, Guid user, int profile, CancellationToken cancel = default);
    Task<SectorResearchOrder> PublishSectorResearchAsync(SectorResearchOrder order, CancellationToken cancel = default);
    Task CancelSectorResearchAsync(Guid id, Guid user, int profileId, CancellationToken cancel = default);
    Task LeaseSectorPlanetAsync(Guid cycle, string sector, int planet, int count, Guid trip, Guid run, bool release, CancellationToken cancel = default);
    Task<long> CompleteSectorPlanetAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, Guid user, int profileId, Guid crewOffer, int experience, CancellationToken cancel = default);
    Task<SectorResearchPayment[]> CompleteSectorPlanetTeamsAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, SectorResearchParticipant[] teams, int experience, CancellationToken cancel = default);
    Task PublishSectorCrewAsync(SectorCrewService service, CancellationToken cancel = default);
    Task<SectorCrewHire> HireSectorCrewAsync(Guid id, Guid offerId, Guid buyer, int profileId, string name, string sector, long expectedPrice, CancellationToken cancel = default);
    Task<SectorCrewHire> SectorHireActionAsync(Guid id, Guid user, int profileId, string action, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<AutomaticSectorResearchRefresh> RefreshAutomaticSectorResearchAsync(Guid cycle, AutomaticSectorResearchTarget[] targets, long now, long refreshInterval = 900000, CancellationToken cancel = default) => RunDbCommand(() => _db.RefreshAutomaticSectorResearchAsync(cycle, targets, now, refreshInterval, cancel));
    public Task<SectorEncounterInfo> SectorEncounterAsync(Guid cycle, string sector, string[] route, double heat, long now, Guid encounter, int defeated, CancellationToken cancel = default) => RunDbCommand(() => _db.SectorEncounterAsync(cycle, sector, route, heat, now, encounter, defeated, cancel));
    public Task DiscoverSectorAsync(Guid cycle, string sector, CancellationToken cancel = default) => RunDbCommand(() => _db.DiscoverSectorAsync(cycle, sector, cancel));
    public Task<SectorResearchBoard> ReadSectorResearchAsync(Guid cycle, Guid user, int profile, CancellationToken cancel = default) => RunDbCommand(() => _db.ReadSectorResearchAsync(cycle, user, profile, cancel));
    public Task<SectorResearchOrder> PublishSectorResearchAsync(SectorResearchOrder order, CancellationToken cancel = default) => RunDbCommand(() => _db.PublishSectorResearchAsync(order, cancel));
    public Task CancelSectorResearchAsync(Guid id, Guid user, int profileId, CancellationToken cancel = default) => RunDbCommand(() => _db.CancelSectorResearchAsync(id, user, profileId, cancel));
    public Task LeaseSectorPlanetAsync(Guid cycle, string sector, int planet, int count, Guid trip, Guid run, bool release, CancellationToken cancel = default) => RunDbCommand(() => _db.LeaseSectorPlanetAsync(cycle, sector, planet, count, trip, run, release, cancel));
    public Task<long> CompleteSectorPlanetAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, Guid user, int profileId, Guid crewOffer, int experience, CancellationToken cancel = default) => RunDbCommand(() => _db.CompleteSectorPlanetAsync(cycle, sector, planet, trip, run, user, profileId, crewOffer, experience, cancel));
    public Task<SectorResearchPayment[]> CompleteSectorPlanetTeamsAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, SectorResearchParticipant[] teams, int experience, CancellationToken cancel = default) => RunDbCommand(() => _db.CompleteSectorPlanetTeamsAsync(cycle, sector, planet, trip, run, teams, experience, cancel));
    public Task PublishSectorCrewAsync(SectorCrewService service, CancellationToken cancel = default) => RunDbCommand(() => _db.PublishSectorCrewAsync(service, cancel));
    public Task<SectorCrewHire> HireSectorCrewAsync(Guid id, Guid offerId, Guid buyer, int profileId, string name, string sector, long expectedPrice, CancellationToken cancel = default) => RunDbCommand(() => _db.HireSectorCrewAsync(id, offerId, buyer, profileId, name, sector, expectedPrice, cancel));
    public Task<SectorCrewHire> SectorHireActionAsync(Guid id, Guid user, int profileId, string action, CancellationToken cancel = default) => RunDbCommand(() => _db.SectorHireActionAsync(id, user, profileId, action, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<AutomaticSectorResearchRefresh> RefreshAutomaticSectorResearchAsync(Guid cycle, AutomaticSectorResearchTarget[] targets, long now, long refreshInterval = 900000, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.RefreshAutomaticSectorResearch(db.DbContext, cycle, targets, now, refreshInterval, cancel); }
    public async Task<SectorEncounterInfo> SectorEncounterAsync(Guid cycle, string sector, string[] route, double heat, long now, Guid encounter, int defeated, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.SectorEncounter(db.DbContext, cycle, sector, route, heat, now, encounter, defeated, cancel); }
    public async Task DiscoverSectorAsync(Guid cycle, string sector, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.DiscoverSector(db.DbContext, cycle, sector, cancel); }
    public async Task<SectorResearchBoard> ReadSectorResearchAsync(Guid cycle, Guid user, int profile, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ReadSectorResearch(db.DbContext, cycle, user, profile, cancel); }
    public async Task<SectorResearchOrder> PublishSectorResearchAsync(SectorResearchOrder order, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.PublishSectorResearch(db.DbContext, order, cancel); }
    public async Task CancelSectorResearchAsync(Guid id, Guid user, int profileId, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.CancelSectorResearch(db.DbContext, id, user, profileId, cancel); }
    public async Task LeaseSectorPlanetAsync(Guid cycle, string sector, int planet, int count, Guid trip, Guid run, bool release, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.LeaseSectorPlanet(db.DbContext, cycle, sector, planet, count, trip, run, release, cancel); }
    public async Task<long> CompleteSectorPlanetAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, Guid user, int profileId, Guid crewOffer, int experience, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.CompleteSectorPlanet(db.DbContext, cycle, sector, planet, trip, run, user, profileId, crewOffer, experience, cancel); }
    public async Task<SectorResearchPayment[]> CompleteSectorPlanetTeamsAsync(Guid cycle, string sector, int planet, Guid trip, Guid run, SectorResearchParticipant[] teams, int experience, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.CompleteSectorPlanetTeams(db.DbContext, cycle, sector, planet, trip, run, teams, experience, cancel); }
    public async Task PublishSectorCrewAsync(SectorCrewService service, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.PublishSectorCrew(db.DbContext, service, cancel); }
    public async Task<SectorCrewHire> HireSectorCrewAsync(Guid id, Guid offerId, Guid buyer, int profileId, string name, string sector, long expectedPrice, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.HireSectorCrew(db.DbContext, id, offerId, buyer, profileId, name, sector, expectedPrice, cancel); }
    public async Task<SectorCrewHire> SectorHireActionAsync(Guid id, Guid user, int profileId, string action, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.SectorHireAction(db.DbContext, id, user, profileId, action, cancel); }
}
