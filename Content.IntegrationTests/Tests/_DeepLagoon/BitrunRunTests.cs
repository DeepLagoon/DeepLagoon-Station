#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared._DeepLagoon.Mapping;
using Content.Server.Mind;
using Content.Server.Actions;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Ghost.Roles.Components;
using Content.Shared.Physics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Research.Components;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunRunTests
{
    [TestCase("ancient_milsim", 75_000, 3)]
    [TestCase("beach_bar", 2_500, 0)]
    [TestCase("breeze_bay", 2_500, 0)]
    [TestCase("island_brawl", 50_000, 3)]
    [TestCase("heretic_hunt", 12_500, 1)]
    [TestCase("colossus", 12_500, 1)]
    public async Task DeliveredCachePaysTheOriginalReceiverOnceAndRestoresTheBody(string domain, int reward, int cost)
    {
        var beachBar = domain == "beach_bar";
#if DEBUG
        cost = 0;
#endif
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var outside = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var session = pair.Server.PlayerMan.Sessions.Single();

        EntityUid body = default;
        EntityUid console = default;
        EntityUid receiver = default;
        EntityUid replacement = default;
        EntityUid server = default;
        EntityUid forge = default;
        EntityUid avatar = default;
        EntityUid map = default;
        Guid matchId = default;

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId);
            maps.SetTiles(outside.Grid, outside.Grid.Comp, Enumerable.Range(-1, 5)
                .SelectMany(x => Enumerable.Range(-1, 4).Select(y => (new Vector2i(x, y), floor))).ToList());
            var environment = entities.EnsureComponent<GridEnvironmentComponent>(outside.Grid);
            environment.SafeAtmosphere = true;
            environment.ForceGravity = true;
            entities.System<GridEnvironmentSystem>().RefreshGrid(outside.Grid, environment);

            var origin = outside.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            body = entities.SpawnEntity("MobHuman", origin.Offset(-Vector2.UnitX));
            console = SpawnOnline("BitrunQuantumConsole", Vector2.Zero);
            server = SpawnOnline("BitrunQuantumServer", Vector2.UnitX);
            var pod = SpawnOnline("BitrunNetpod", new Vector2(-1, 1));
            if (domain == "beach_bar")
                entities.GetComponent<BitrunPodComponent>(pod).Program = "BitrunDiskForceWall";
            forge = SpawnOnline("BitrunByteforge", new Vector2(1, 1));
            receiver = SpawnOnline("ResearchAndDevelopmentServer", new Vector2(2, 0));
            replacement = SpawnOnline("ResearchAndDevelopmentServer", new Vector2(2, 1));
            entities.GetComponent<BitrunServerComponent>(server).Credits = cost;

            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(session.UserId);
            minds.TransferTo(mind.Owner, body);

            Send(console, BitrunUiAction.BindResearch, target: entities.GetNetEntity(receiver));
            Assert.That(entities.GetComponent<ResearchClientComponent>(console).Server, Is.EqualTo(receiver));
            Send(console, BitrunUiAction.Start, domain);
            var entry = SessionFor(entities, console, body);
            Assert.That(entry.Waiting, Is.True);
            matchId = entry.Id;
            Assert.That(entities.GetComponent<BitrunServerComponent>(server).Credits, Is.EqualTo(cost));
            Send(console, BitrunUiAction.BindResearch, target: entities.GetNetEntity(replacement));
            var podEntry = SessionFor(entities, pod, body);
            Assert.That(podEntry.Id, Is.EqualTo(entry.Id));
            Send(pod, BitrunUiAction.Enter, target: podEntry.Server, side: BitrunSide.Attackers, match: podEntry.Id);
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));

            EntityUid SpawnOnline(string prototype, Vector2 offset)
            {
                var uid = entities.SpawnEntity(prototype, origin.Offset(offset));
                if (entities.HasComponent<ApcPowerReceiverComponent>(uid))
                    entities.RemoveComponent<ApcPowerReceiverComponent>(uid);
                return uid;
            }
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<BitrunServerComponent>(server).Credits, Is.Zero,
                "The domain costs credits at launch.");

            Assert.That(session.AttachedEntity, Is.Not.EqualTo(body), "Entering the pod must attach the real mind to the avatar.");
            avatar = session.AttachedEntity!.Value;
            Assert.That(entities.HasComponent<BitrunAvatarComponent>(avatar), Is.True);
            map = entities.GetComponent<TransformComponent>(avatar).MapUid!.Value;
            Assert.That(map, Is.Not.EqualTo(entities.GetComponent<TransformComponent>(body).MapUid));
            Assert.That(entities.GetComponent<MapLightComponent>(map).AmbientLightColor, Is.EqualTo(Color.Black),
                "Ambient light is supplied by source area cells, not a white fill across the whole map.");
            Assert.That(entities.HasComponent<BitrunAreaLightingComponent>(entities.GetComponent<TransformComponent>(avatar).GridUid), Is.True);
            if (domain == "beach_bar")
            {
                var actions = entities.System<ActionsSystem>();
                var wallAction = actions.GetActions(avatar).Single(action => action.Comp.BaseEvent is BitrunForceWallEvent);
                actions.PerformAction(avatar, null, wallAction.Id, wallAction.Comp, wallAction.Comp.BaseEvent, TimeSpan.Zero);
                var walls = InDomain<MetaDataComponent>(entities, map).Where(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunForceWall").ToArray();
                Assert.That(walls, Has.Length.EqualTo(3));
                Assert.That(walls.All(uid => entities.GetComponent<PreventCollideComponent>(uid).Uid == avatar), Is.True);
            }

            if (domain == "ancient_milsim")
            {
                foreach (var enemy in InDomain<MetaDataComponent>(entities, map).Where(uid =>
                             entities.HasComponent<BitrunMilsimEnemyComponent>(uid) || entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID is
                                 "BitrunMilsimInfantry" or "BitrunMilsimRifleman").ToArray())
                    entities.DeleteEntity(enemy);

                var stage = InDomain<BitrunScenarioMarkerComponent>(entities, map).Single(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityName == "ancient_stage");
                var xform = entities.System<SharedTransformSystem>();
                xform.SetCoordinates(avatar, entities.GetComponent<TransformComponent>(stage).Coordinates);
                entities.EventBus.RaiseLocalEvent(stage, new InteractHandEvent(avatar, stage));
            }
        });

        switch (domain)
        {
            case "beach_bar":
                await DrinkBeachBarCocktails();
                break;
            case "breeze_bay":
                await FishAtBreezeBay();
                break;
            case "island_brawl":
                await DefeatIslandParticipants();
                break;
            case "heretic_hunt":
                await SacrificeCrew();
                break;
            case "colossus":
                await DefeatBoss();
                break;
            default:
                await pair.RunTicksSync(5 * pair.Server.Timing.TickRate + 1);
                break;
        }

        await pair.Server.WaitAssertion(() =>
        {
            var cache = MainCaches().Single();
            var goal = InDomain<BitrunCacheGoalComponent>(entities, map).First();
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.Zero);
            entities.AddComponent<ApcPowerReceiverComponent>(beachBar ? forge : receiver);
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(avatar, entities.GetComponent<TransformComponent>(cache).Coordinates);
            Assert.That(entities.System<PullingSystem>().TryStartPull(avatar, cache), Is.True);
            var goalLocation = entities.GetComponent<TransformComponent>(goal).Coordinates;
            transform.SetCoordinates(avatar, goalLocation);
            transform.SetCoordinates(cache, beachBar ? goalLocation.Offset(new Vector2(0.499f, 0.499f)) : goalLocation);
            if (beachBar)
            {
                // The crate crosses the upload tile and leaves it within one server tick.
                transform.SetCoordinates(cache, goalLocation.Offset(Vector2.UnitX));
            }
        });

        await pair.RunTicksSync(2 * pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(MainCaches(), Is.Empty);
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(beachBar ? reward : 0));
            Assert.That(InDomain<MetaDataComponent>(entities, outside.MapUid).Count(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunDecryptedCache"),
                Is.EqualTo(beachBar ? 0 : 1));
            entities.RemoveComponent<ApcPowerReceiverComponent>(beachBar ? forge : receiver);
        });

        await pair.RunTicksSync(2 * pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(reward));
            Assert.That(entities.GetComponent<ResearchServerComponent>(replacement).Points, Is.Zero);
            Assert.That(entities.System<BitrunSystem>().GetPersonalResearchPoints(session.UserId), Is.EqualTo(reward));
            Assert.That(MainCaches(), Is.Empty);

            var exit = InDomain<BitrunExitComponent>(entities, map).First();
            entities.System<SharedTransformSystem>().SetCoordinates(avatar, entities.GetComponent<TransformComponent>(exit).Coordinates);
            entities.EventBus.RaiseLocalEvent(exit, new InteractHandEvent(avatar, exit));
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            Assert.That(entities.HasComponent<BitrunBodyComponent>(body), Is.False);
            Send(console, BitrunUiAction.Start, "beach_bar");
            Assert.That(SessionFor(entities, console, body).Waiting, Is.True);
            Assert.That(SessionFor(entities, console, body).Id, Is.Not.EqualTo(matchId));
            Assert.That(entities.EntityExists(map), Is.False);
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(reward));
            Assert.That(entities.GetComponent<ResearchServerComponent>(replacement).Points, Is.Zero);
            Assert.That(entities.System<BitrunSystem>().GetPersonalResearchPoints(session.UserId), Is.EqualTo(reward));
            Assert.That(InDomain<MetaDataComponent>(entities, outside.MapUid).Count(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunDecryptedCache"), Is.EqualTo(1));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();

        async Task DefeatBoss()
        {
            EntityUid boss = default;
            EntityCoordinates deathLocation = default;
            await pair.Server.WaitAssertion(() =>
            {
                boss = InDomain<BitrunColossusComponent>(entities, map).Single();
                Assert.That(MainCaches(), Is.Empty, "Appearance variants must still require defeating the domain boss.");
                entities.GetComponent<Content.Server.NPC.HTN.HTNComponent>(boss).Enabled = false;
                deathLocation = entities.GetComponent<TransformComponent>(boss).Coordinates;
                var damage = new Content.Shared.Damage.DamageSpecifier();
                damage.DamageDict["Blunt"] = 2000;
                entities.System<Content.Shared.Damage.DamageableSystem>().TryChangeDamage(boss, damage, true, ignoreGlobalModifiers: true);
                var cache = MainCaches().Single();
                var cacheLocation = entities.GetComponent<TransformComponent>(cache).Coordinates;
                Assert.That(cacheLocation.TryDistance(entities, deathLocation, out var distance), Is.True);
                Assert.That(distance, Is.LessThan(0.1f), "The objective must record the death location synchronously before deletion.");
            });
            await pair.RunTicksSync(2 * pair.Server.Timing.TickRate);
            await pair.Server.WaitAssertion(() =>
            {
                Assert.That(entities.EntityExists(boss), Is.False, "Source colossus disappears after death.");
                Assert.That(MainCaches().Count(), Is.EqualTo(1));
            });
        }

        async Task FishAtBreezeBay()
        {
            EntityUid spot = default, rod = default;
            await pair.Server.WaitAssertion(() =>
            {
                spot = InDomain<BitrunScenarioMarkerComponent>(entities, map).Single(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunFishingSpot");
                rod = InDomain<BitrunFishingRodComponent>(entities, map).First();
                var xform = entities.System<SharedTransformSystem>();
                var coordinates = entities.GetComponent<TransformComponent>(spot).Coordinates;
                xform.SetCoordinates(avatar, coordinates);
                xform.SetCoordinates(rod, coordinates);
                Assert.That(entities.System<SharedHandsSystem>().TryPickupAnyHand(avatar, rod), Is.True);
            });

            for (var caught = 1; caught <= 5; caught++)
            {
                await pair.Server.WaitAssertion(() =>
                {
                    var use = new InteractUsingEvent(avatar, rod, spot, entities.GetComponent<TransformComponent>(spot).Coordinates);
                    entities.EventBus.RaiseLocalEvent(spot, use);
                    Assert.That(use.Handled, Is.True);
                    AdvanceTo(pair, pair.Server.Timing.CurTime + TimeSpan.FromSeconds(7));
                });
                await pair.RunTicksSync(3);
                await pair.Server.WaitAssertion(() =>
                {
                    Assert.That(InDomain<MetaDataComponent>(entities, map).Count(uid =>
                        entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "FoodMeatFish"), Is.EqualTo(caught));
                    Assert.That(MainCaches().Any(), Is.EqualTo(caught == 5));
                });
            }

            await pair.Server.WaitAssertion(() =>
                Assert.That(entities.System<SharedHandsSystem>().TryDrop(avatar, rod), Is.True));
        }

        async Task SacrificeCrew()
        {
            EntityUid[] victims = [];
            EntityUid rune = default;
            await pair.Server.WaitAssertion(() =>
            {
                rune = InDomain<BitrunScenarioMarkerComponent>(entities, map).Single(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityName == "loot_signal");
                victims = InDomain<BitrunRitualOfferingComponent>(entities, map).OrderByDescending(uid =>
                    entities.GetComponent<BitrunRitualOfferingComponent>(uid).Value).Take(4).ToArray();
                Assert.That(victims.Sum(uid => entities.GetComponent<BitrunRitualOfferingComponent>(uid).Value), Is.GreaterThanOrEqualTo(10));
            });
            foreach (var victim in victims)
            {
                await pair.Server.WaitAssertion(() =>
                {
                    entities.System<MobStateSystem>().ChangeMobState(victim, MobState.Dead);
                    entities.System<SharedTransformSystem>().SetCoordinates(victim,
                        entities.GetComponent<TransformComponent>(rune).Coordinates);
                });
                await pair.RunTicksSync(2);
            }
        }

        async Task DefeatIslandParticipants()
        {
            EntityUid[] spawners = [];
            await pair.Server.WaitAssertion(() =>
            {
                spawners = InDomain<GhostRoleMobSpawnerComponent>(entities, map).Where(uid =>
                    entities.GetComponent<GhostRoleMobSpawnerComponent>(uid).Prototype?.Id ==
                        "BitrunRoleBodyObjEffectMobSpawnGhostRoleHumanVirtualDomainIslander").Take(10).ToArray();
                Assert.That(spawners, Has.Length.EqualTo(10));
                Assert.That(InDomain<MetaDataComponent>(entities, map).Any(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunIslandCrab"), Is.False);
            });
            for (var index = 0; index < spawners.Length; index++)
            {
                var selected = spawners[index];
                await pair.Server.WaitAssertion(() =>
                {
                    var bodyPrototype = entities.GetComponent<GhostRoleMobSpawnerComponent>(selected).Prototype;
                    var guest = entities.SpawnEntity(bodyPrototype, entities.GetComponent<TransformComponent>(selected).Coordinates);
                    entities.DeleteEntity(selected);
                    Assert.That(entities.GetComponent<BitrunDomainGuestComponent>(guest).Islander, Is.True);
                    entities.System<MobStateSystem>().ChangeMobState(guest, MobState.Dead);
                    Assert.That(entities.GetComponent<BitrunDomainGuestComponent>(guest).DeathCounted, Is.True);
                });
                await pair.RunTicksSync(2);
                await pair.Server.WaitAssertion(() => Assert.That(MainCaches().Any(), Is.EqualTo(index == 9)));
            }
        }

        async Task DrinkBeachBarCocktails()
        {
            EntityUid[] cocktails = [];
            await pair.Server.WaitAssertion(() =>
            {
                cocktails = InDomain<MetaDataComponent>(entities, map).Where(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype is { } prototype &&
                    (prototype.ID == "DrinkPinaColadaGlass" || prototype.ID.StartsWith(
                        "BitrunMapItemReagentContainersCupGlassDrinkingglassFilledPinaColada", StringComparison.Ordinal))).OrderByDescending(uid =>
                    entities.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID, StringComparer.Ordinal).ToArray();
                Assert.That(cocktails, Has.Length.EqualTo(4));
            });

            for (var index = 0; index < cocktails.Length; index++)
            {
                var cocktail = cocktails[index];
                await pair.Server.WaitAssertion(() =>
                {
                    var hands = entities.System<SharedHandsSystem>();
                    var solutions = entities.System<SharedSolutionContainerSystem>();
                    var organ = entities.System<BodySystem>().GetBodyOrganEntityComps<StomachComponent>(avatar).Single().Owner;
                    Assert.That(solutions.TryGetSolution(organ, StomachSystem.DefaultSolutionName, out var stomach), Is.True);
                    solutions.RemoveAllSolution(stomach!.Value);

                    entities.System<SharedTransformSystem>().SetCoordinates(cocktail,
                        entities.GetComponent<TransformComponent>(avatar).Coordinates);
                    Assert.That(hands.TryPickupAnyHand(avatar, cocktail), Is.True);
                    Assert.That(entities.System<SharedInteractionSystem>().UseInHandInteraction(avatar, cocktail), Is.True);
                });

                for (var sip = 1; sip <= 6; sip++)
                {
                    await pair.Server.WaitAssertion(() => AdvanceTo(pair, pair.Server.Timing.CurTime + TimeSpan.FromSeconds(2)));
                    await pair.RunTicksSync(2);
                    if (index == 3 && sip is 1 or 2)
                    {
                        var revealed = sip == 2;
                        await pair.Server.WaitAssertion(() =>
                            Assert.That(MainCaches().Any(), Is.EqualTo(revealed)));
                    }
                }

                await pair.Server.WaitAssertion(() =>
                {
                    var solutions = entities.System<SharedSolutionContainerSystem>();
                    Assert.That(solutions.TryGetSolution(cocktail, "drink", out _, out var drink), Is.True);
                    Assert.That(drink!.Volume.Float(), Is.Zero);
                    Assert.That(MainCaches().Count(), Is.EqualTo(index == 3 ? 1 : 0));
                    Assert.That(entities.System<SharedHandsSystem>().TryDrop(avatar, cocktail), Is.True);
                });
            }
        }

        IEnumerable<EntityUid> MainCaches() => InDomain<BitrunCacheComponent>(entities, map)
            .Where(uid => entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunEncryptedCache");

        void Send(EntityUid device, BitrunUiAction action, string? id = null, NetEntity? target = null,
            BitrunSide? side = null, Guid? match = null)
        {
            entities.EventBus.RaiseLocalEvent(device,
                new BitrunUiMessage(action, id, target, side, match) { Actor = body, UiKey = BitrunUiKey.Key });
        }
    }

    [Test]
    public async Task LateDefendersShareThePayoutAfterTheAttackerDies()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var outside = await pair.CreateTestMap();
        var players = await pair.Server.AddDummySessions(3);
        foreach (var player in players)
            await PoolManager.WaitUntil(pair.Server, () => player.Status == SessionStatus.InGame, 600);

        var entities = pair.Server.EntMan;
        var bodies = new EntityUid[players.Length];
        var pods = new EntityUid[players.Length];
        EntityUid receiver = default;
        EntityUid console = default;
        EntityUid server = default;
        EntityUid avatar = default;
        EntityUid domainMap = default;
        BitrunSessionEntry match = null!;

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId);
            maps.SetTiles(outside.Grid, outside.Grid.Comp, Enumerable.Range(-2, 9)
                .SelectMany(x => Enumerable.Range(-2, 5).Select(y => (new Vector2i(x, y), floor))).ToList());

            var origin = outside.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            console = SpawnOnline("BitrunQuantumConsole", Vector2.Zero);
            server = SpawnOnline("BitrunQuantumServer", Vector2.UnitX);
            var otherServer = SpawnOnline("BitrunQuantumServer", new Vector2(5, 0));
            receiver = SpawnOnline("ResearchAndDevelopmentServer", new Vector2(0, -1));
            entities.GetComponent<BitrunServerComponent>(server).Credits = 3;
            for (var index = 0; index < players.Length; index++)
            {
                var offset = new Vector2(index == 0 ? 0 : 5, 1 - index);
                if (index > 0)
                    offset += Vector2.UnitX;
                bodies[index] = entities.SpawnEntity("MobHuman", origin.Offset(offset + new Vector2(0.75f, 0f)));
                pods[index] = SpawnOnline("BitrunNetpod", offset);
                var minds = entities.System<MindSystem>();
                var mind = minds.GetOrCreateMind(players[index].UserId);
                minds.TransferTo(mind.Owner, bodies[index]);
            }

            Send(console, bodies[0], new BitrunUiMessage(BitrunUiAction.BindResearch, target: entities.GetNetEntity(receiver)));
            Send(console, bodies[0], new BitrunUiMessage(BitrunUiAction.Start, "ancient_milsim"));
            match = SessionFor(entities, pods[0], bodies[0]);
            Send(pods[0], bodies[0], new BitrunUiMessage(BitrunUiAction.Enter, target: match.Server,
                side: BitrunSide.Attackers, session: match.Id));

            Assert.That(match.Server, Is.EqualTo(entities.GetNetEntity(server)));
            Assert.That(match.Server, Is.Not.EqualTo(entities.GetNetEntity(otherServer)));
            Assert.That(players[0].AttachedEntity, Is.EqualTo(bodies[0]));

            EntityUid SpawnOnline(string prototype, Vector2 offset)
            {
                var uid = entities.SpawnEntity(prototype, origin.Offset(offset));
                if (entities.HasComponent<ApcPowerReceiverComponent>(uid))
                    entities.RemoveComponent<ApcPowerReceiverComponent>(uid);
                return uid;
            }
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() =>
        {
            avatar = players[0].AttachedEntity!.Value;
            domainMap = entities.GetComponent<TransformComponent>(avatar).MapUid!.Value;
            Assert.That(entities.GetComponent<BitrunAvatarComponent>(avatar).Side, Is.EqualTo(BitrunSide.Attackers));
            for (var index = 1; index < players.Length; index++)
            {
                var active = SessionFor(entities, pods[index], bodies[index]);
                Assert.That(active.Waiting, Is.False);
                Assert.That(active.Id, Is.EqualTo(match.Id));
                Send(pods[index], bodies[index], new BitrunUiMessage(BitrunUiAction.Enter, target: active.Server,
                    side: BitrunSide.Defenders, session: Guid.NewGuid()));
                Assert.That(players[index].AttachedEntity, Is.EqualTo(bodies[index]), "A stale session ID must fail.");
                Send(pods[index], bodies[index], new BitrunUiMessage(BitrunUiAction.Enter, target: active.Server,
                    side: BitrunSide.Defenders, session: active.Id));
                var defender = players[index].AttachedEntity!.Value;
                Assert.That(entities.GetComponent<BitrunAvatarComponent>(defender).Side, Is.EqualTo(BitrunSide.Defenders));
                Assert.That(entities.GetComponent<TransformComponent>(defender).MapUid, Is.EqualTo(domainMap));
            }
            entities.System<MobStateSystem>().ChangeMobState(avatar, MobState.Dead);
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(players[0].AttachedEntity, Is.EqualTo(bodies[0]));
            var bitrun = entities.System<BitrunSystem>();
            Assert.That(bitrun.GetPersonalResearchPoints(players[0].UserId), Is.Zero);
            Assert.That(bitrun.GetPersonalResearchPoints(players[1].UserId), Is.EqualTo(37_500));
            Assert.That(bitrun.GetPersonalResearchPoints(players[2].UserId), Is.EqualTo(37_500));
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(75_000));
            Assert.That(InDomain<MetaDataComponent>(entities, outside.MapUid).Any(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunDecryptedCache"), Is.False);
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(75_000));
            for (var index = 1; index < players.Length; index++)
            {
                var defender = players[index].AttachedEntity!.Value;
                entities.EventBus.RaiseLocalEvent(defender, new BitrunDisconnectEvent());
                Assert.That(players[index].AttachedEntity, Is.EqualTo(bodies[index]));
            }

            Send(console, bodies[0], new BitrunUiMessage(BitrunUiAction.Stop, session: match.Id));
            Assert.That(entities.EntityExists(domainMap), Is.False);
        });

        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();

        void Send(EntityUid device, EntityUid body, BitrunUiMessage message)
        {
            message.Actor = body;
            message.UiKey = BitrunUiKey.Key;
            entities.EventBus.RaiseLocalEvent(device, message);
        }
    }

    private static BitrunSessionEntry SessionFor(IEntityManager entities, EntityUid device, EntityUid actor)
    {
        entities.EventBus.RaiseLocalEvent(device, new BoundUIOpenedEvent(BitrunUiKey.Key, device, actor));
        Assert.That(entities.System<SharedUserInterfaceSystem>().TryGetUiState<BitrunUiState>(device, BitrunUiKey.Key, out var state), Is.True);
        return state!.Sessions.Single();
    }

    private static void AdvanceTo(TestPair pair, TimeSpan deadline)
    {
        var timing = pair.Server.Timing;
        timing.TimeBase = (deadline, timing.CurTick);
    }

    private static IEnumerable<EntityUid> InDomain<T>(IEntityManager entities, EntityUid map)
        where T : IComponent
    {
        var query = entities.AllEntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapUid == map)
                yield return uid;
        }
    }
}
