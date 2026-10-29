using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NF.Salvage;
using Content.Server.NPC.HTN;
using Content.Server.Salvage.Expeditions;
using Content.Server.Spawners.Components;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Mind;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SalvageExpeditionHostilityTests
{
    [Test]
    public async Task DefendersTargetEveryCrewFactionDespiteFriendshipAndIgnoreExceptions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = CreateExpedition(entities, pair.Server.ResolveDependency<ITileDefinitionManager>());
            var coords = new EntityCoordinates(map, new Vector2(0.5f, 0.5f));
            var factions = entities.System<NpcFactionSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var crew = entities.SpawnEntity("MobHuman", coords);
            Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(crew), Is.False);
            var defenders = new[]
            {
                "NFMobXeno", "NFMobXenoQueen", "NFMobCarp", "MobDragonDungeon",
                "MobSyndicateNavalCommanderA", "MobBloodCultistAscended", "MobHorrorExpeditions",
                "MobArgocyteLeviathingExpeditions", "MobDinosaurTrexExpeditions", "MobMercenaryCaptain",
                "MobExplorerBoss", "MobRogueSiliconBoss", "MobPunkGangerLeader",
                "WeaponTurretSyndicate", "WeaponTurretXeno", "BloodCultTurret"
            }.Select(id => entities.SpawnEntity(id, coords)).ToArray();

            foreach (var defender in defenders)
            {
                Assert.That(entities.GetComponent<NpcForcedHostilityComponent>(defender).Group, Is.EqualTo(map));
                factions.IgnoreEntity(defender, crew);
                Assert.That(factions.IsIgnored(defender, crew), Is.False);
            }

            var crewFactions = prototypes.EnumeratePrototypes<NpcFactionPrototype>().ToArray();
            foreach (var faction in crewFactions)
            {
                factions.ClearFactions(crew);
                factions.AddFaction(crew, faction.ID);
                foreach (var defender in defenders)
                {
                    Assert.That(factions.IsEntityFriendly(defender, crew), Is.False, faction.ID);
                    Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(crew), faction.ID);
                    Assert.That(factions.GetNearbyFriendlies(defender, 8f), Does.Not.Contain(crew), faction.ID);
                }
            }

            factions.ClearFactions(crew);
            foreach (var defender in defenders)
            {
                factions.ClearFactions(defender);
                Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(crew), "Factionless crew and defenders must still be enemies.");
            }
            TestContext.Out.WriteLine($"Checked {defenders.Length} defenders against all {crewFactions.Length} NPC factions and factionless crew.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AllMissionSpawnsAndTheirPossibleChildrenReceiveHostility()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = CreateExpedition(entities, pair.Server.ResolveDependency<ITileDefinitionManager>());
            var coords = new EntityCoordinates(map, new Vector2(0.5f, 0.5f));
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var visited = new HashSet<EntProtoId>();
            void CheckSpawn(EntProtoId id)
            {
                if (!visited.Add(id))
                    return;
                var prototype = prototypes.Index(id);
                foreach (var registry in prototype.Components.Values)
                {
                    if (registry.Component is RandomSpawnerComponent random)
                        foreach (var child in random.Prototypes.Concat(random.RarePrototypes))
                            CheckSpawn(child);
                    if (registry.Component is TimedSpawnerComponent timed)
                        foreach (var child in timed.Prototypes)
                            CheckSpawn(child);
                }

                var spawned = entities.SpawnEntity(id, coords);
                if (!entities.EntityExists(spawned) || !entities.HasComponent<HTNComponent>(spawned))
                    return;
                Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(spawned), Is.True, id.Id);
                Assert.That(entities.GetComponent<NpcForcedHostilityComponent>(spawned).Group, Is.EqualTo(map), id.Id);
            }

            foreach (var faction in prototypes.EnumeratePrototypes<SalvageFactionPrototype>())
            {
                foreach (var entry in faction.MobGroups.SelectMany(group => group.Entries))
                    if (entry.PrototypeId is { } id)
                        CheckSpawn(id);
                foreach (var key in new[] { "Megafauna", "DefenseStructure" })
                    if (faction.Configs.TryGetValue(key, out var id))
                        CheckSpawn(id);
            }
            TestContext.Out.WriteLine($"Checked {visited.Count} mission prototypes, including common, rare and timed spawner children.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VisitingPetsStayFriendlyAndDefendersRemainHostileAfterBoarding()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var xforms = entities.System<SharedTransformSystem>();
            var factions = entities.System<NpcFactionSystem>();
            var map = CreateExpedition(entities, pair.Server.ResolveDependency<ITileDefinitionManager>());
            var coords = new EntityCoordinates(map, new Vector2(0.5f, 0.5f));
            var home = maps.CreateMap(out var homeId);
            var homeGrid = maps.CreateGridEntity(homeId);
            maps.SetTile(homeGrid, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            var homeCoords = new EntityCoordinates(homeGrid, new Vector2(0.5f, 0.5f));
            var crew = entities.SpawnEntity("MobHuman", homeCoords);
            factions.ClearFactions(crew);
            factions.AddFaction(crew, "Chimera");
            var pet = entities.SpawnEntity("MobCarp", homeCoords);
            factions.IgnoreEntity(pet, crew);
            var ordinaryFlesh = entities.SpawnEntity("MobHorrorExpeditions", homeCoords);
            Assert.That(factions.IsEntityFriendly(ordinaryFlesh, crew), Is.True);
            Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(ordinaryFlesh), Is.False);

            var defender = entities.SpawnEntity("MobHorrorExpeditions", coords);
            var ally = entities.SpawnEntity("MobHorrorExpeditions", coords);
            xforms.SetCoordinates(homeGrid, new EntityCoordinates(map, Vector2.Zero));
            Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(pet), Is.False);
            Assert.That(factions.IsIgnored(pet, crew), Is.True);
            // Newly spawned crew bodies on the expedition terrain must not become defenders either.
            var replacementBody = entities.SpawnEntity("MobHuman", coords);
            Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(replacementBody), Is.False);

            var lateDefender = entities.SpawnEntity("MobHorrorExpeditions", coords);
            Assert.That(entities.GetComponent<NpcForcedHostilityComponent>(lateDefender).Group, Is.EqualTo(map));
            Assert.That(entities.GetComponent<NpcForcedHostilityComponent>(defender).Group, Is.EqualTo(map));
            var newPet = entities.SpawnEntity("MobCarp", homeCoords);
            Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(newPet), Is.False);
            Assert.That(factions.IsEntityFriendly(defender, ally), Is.True);
            Assert.That(factions.IsEntityFriendly(defender, crew), Is.False);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(crew));
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(ally));
            var minds = entities.System<SharedMindSystem>();
            minds.TransferTo(minds.CreateMind(null, "Expedition crew"), ally);
            Assert.That(factions.IsEntityFriendly(defender, ally), Is.False, "Taking over a defender must not grant immunity to crew.");
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(ally));
            xforms.SetCoordinates(defender, homeCoords);
            factions.IgnoreEntity(defender, crew);
            Assert.That(entities.HasComponent<PacifiedComponent>(defender), Is.False);
            Assert.That(entities.HasComponent<TimedDespawnComponent>(defender), Is.True);
            Assert.That(entities.GetComponent<NFSalvageMobRestrictionsComponent>(ordinaryFlesh).AddComponentsLeaveGrid.ContainsKey("Pacified"), Is.True);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(crew));
            xforms.SetCoordinates(homeGrid, new EntityCoordinates(home, Vector2.Zero));
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(crew));
            Assert.That(factions.IsIgnored(pet, crew), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    private static EntityUid CreateExpedition(IEntityManager entities, ITileDefinitionManager tiles)
    {
        var maps = entities.System<SharedMapSystem>();
        var map = maps.CreateMap(out var mapId, runMapInit: false);
        var grid = entities.EnsureComponent<MapGridComponent>(map);
        maps.SetTile((map, grid), Vector2i.Zero, new Tile(tiles["Plating"].TileId));
        maps.InitializeMap(mapId);
        var expedition = entities.AddComponent<SalvageExpeditionComponent>(map);
        expedition.Station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
        expedition.MissionParams = new SalvageMissionParams { MissionType = SalvageMissionType.Elimination };
        expedition.Rewards = [];
        maps.SetPaused(mapId, true);
        return map;
    }
}
