using System.Linq;
using System.Numerics;
using Content.Shared.Mind;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SyndicateNpcHostilityTests
{
    [Test]
    public async Task SyndicateNpcsTargetOtherPlayerFactionsButKeepSyndicateAllied()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out _);
            var coords = new EntityCoordinates(map, Vector2.Zero);
            var factions = entities.System<NpcFactionSystem>();
            var minds = entities.System<SharedMindSystem>();
            var player = entities.SpawnEntity("MobHuman", coords);
            minds.TransferTo(minds.CreateMind(null, "Hostility test player"), player);
            var defenders = new[]
            {
                "MobSyndicateFootsoldier", "MobSyndicateNavalCommanderA", "WeaponTurretSyndicate"
            }.Select(id => entities.SpawnEntity(id, coords)).ToArray();

            foreach (var defender in defenders)
            {
                Assert.That(entities.HasComponent<NpcForcedHostilityComponent>(defender), Is.False);
                factions.IgnoreEntity(defender, player);
            }

            var playerFactions = pair.Server.ResolveDependency<IPrototypeManager>()
                .EnumeratePrototypes<NpcFactionPrototype>().ToArray();
            foreach (var faction in playerFactions)
            {
                factions.ClearFactions(player);
                factions.AddFaction(player, faction.ID);
                AssertPlayerRelationship(faction.ID == "Syndicate");
                // Syndicate membership grants alliance even on a multi-faction body.
                factions.AddFaction(player, "Syndicate");
                factions.AddFaction(player, "SyndicateNF");
                AssertPlayerRelationship(allied: true);
            }

            // Expedition defenders must preserve this explicit player alliance too.
            foreach (var defender in defenders)
                entities.AddComponent<NpcForcedHostilityComponent>(defender).Group = map;
            AssertPlayerRelationship(allied: true);
            factions.ClearFactions(player);
            factions.AddFaction(player, "SyndicateNF");
            AssertPlayerRelationship(allied: false);

            factions.ClearFactions(player);
            AssertPlayerRelationship(allied: false);
            entities.RemoveComponent<NpcFactionMemberComponent>(player);
            AssertPlayerRelationship(allied: false);

            entities.System<SharedTransformSystem>().SetCoordinates(player,
                new EntityCoordinates(map, new Vector2(20, 0)));
            foreach (var defender in defenders)
                Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(player));

            void AssertPlayerRelationship(bool allied)
            {
                foreach (var defender in defenders)
                {
                    Assert.That(factions.IsEntityFriendly(defender, player), Is.EqualTo(allied));
                    Assert.That(factions.IsIgnored(defender, player), Is.EqualTo(allied));
                    Assert.That(factions.GetNearbyHostiles(defender, 8f).Count(target => target == player), Is.EqualTo(allied ? 0 : 1));
                    Assert.That(factions.GetNearbyFriendlies(defender, 8f).Contains(player), Is.EqualTo(allied));
                    Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(defender));
                }
            }

            TestContext.Out.WriteLine($"Checked {defenders.Length} Syndicate NPCs against all {playerFactions.Length} factions, Syndicate allies, multiple factions, factionless players and expedition hostility.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NpcAlliesAndPlayerControlledSyndicateKeepOrdinaryRelationships()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out _);
            var coords = new EntityCoordinates(map, Vector2.Zero);
            var factions = entities.System<NpcFactionSystem>();
            var minds = entities.System<SharedMindSystem>();
            var defender = entities.SpawnEntity("MobSyndicateNavalCommanderA", coords);
            var ally = entities.SpawnEntity("MobSyndicateFootsoldier", coords);

            Assert.That(factions.IsEntityFriendly(defender, ally), Is.True);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(ally));

            var mind = minds.CreateMind(null, "Syndicate player");
            minds.TransferTo(mind, ally);
            Assert.That(factions.IsEntityFriendly(defender, ally), Is.True);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(ally));
            factions.ClearFactions(ally);
            factions.AddFaction(ally, "SyndicateNF");
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Contain(ally));
            Assert.That(factions.IsEntityFriendly(ally, defender), Is.True);
            Assert.That(factions.GetNearbyHostiles(ally, 8f), Does.Not.Contain(defender));

            factions.AddFaction(ally, "Syndicate");
            Assert.That(factions.IsEntityFriendly(defender, ally), Is.True);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(ally));
            minds.TransferTo(mind, null);
            Assert.That(factions.IsEntityFriendly(defender, ally), Is.True);
            Assert.That(factions.GetNearbyHostiles(defender, 8f), Does.Not.Contain(ally));

            var ordinaryNpc = entities.SpawnEntity("MobCivilian", coords);
            var crew = entities.SpawnEntity("MobHuman", coords);
            minds.TransferTo(mind, crew);
            Assert.That(factions.IsEntityFriendly(ordinaryNpc, crew), Is.True);
            Assert.That(factions.GetNearbyHostiles(ordinaryNpc, 8f), Does.Not.Contain(crew));
        });
        await pair.CleanReturnAsync();
    }
}
