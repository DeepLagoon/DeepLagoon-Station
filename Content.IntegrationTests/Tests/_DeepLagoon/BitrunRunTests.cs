#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server._DeepLagoon.Mapping;
using Content.Shared._DeepLagoon.Mapping;
using Content.Server.Mind;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
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
    [Test]
    public async Task AncientMilsimPaysTheOriginalReceiverOnceAndRestoresTheBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var outside = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var session = pair.Server.PlayerMan.Sessions.Single();

        EntityUid body = default;
        EntityUid console = default;
        EntityUid receiver = default;
        EntityUid replacement = default;
        EntityUid server = default;
        EntityUid avatar = default;
        EntityUid map = default;
        EntityUid stage = default;
        TimeSpan launchAt = default;
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
            SpawnOnline("BitrunByteforge", new Vector2(1, 1));
            receiver = SpawnOnline("ResearchAndDevelopmentServer", new Vector2(2, 0));
            replacement = SpawnOnline("ResearchAndDevelopmentServer", new Vector2(2, 1));
            entities.GetComponent<BitrunServerComponent>(server).Credits = 3;

            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(session.UserId);
            minds.TransferTo(mind.Owner, body);

            Send(console, BitrunUiAction.BindResearch, target: entities.GetNetEntity(receiver));
            Assert.That(entities.GetComponent<ResearchClientComponent>(console).Server, Is.EqualTo(receiver));
            Send(console, BitrunUiAction.Start, "ancient_milsim");
            var entry = SessionFor(entities, console, body);
            Assert.That(entry.Waiting, Is.True);
            launchAt = entry.Deadline;
            matchId = entry.Id;
            Assert.That(launchAt - pair.Server.Timing.CurTime, Is.InRange(TimeSpan.FromTicks(1), TimeSpan.FromMinutes(3)));
            Assert.That(entities.GetComponent<BitrunServerComponent>(server).Credits, Is.EqualTo(3));
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

        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            AdvanceTo(pair, launchAt);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<BitrunServerComponent>(server).Credits, Is.Zero,
                "The domain costs credits at launch.");

            Assert.That(session.AttachedEntity, Is.Not.EqualTo(body), "Entering the pod must attach the real mind to the avatar.");
            avatar = session.AttachedEntity!.Value;
            Assert.That(entities.HasComponent<BitrunAvatarComponent>(avatar), Is.True);
            map = entities.GetComponent<TransformComponent>(avatar).MapUid!.Value;
            Assert.That(map, Is.Not.EqualTo(entities.GetComponent<TransformComponent>(body).MapUid));
            Assert.That(entities.GetComponent<MapLightComponent>(map).AmbientLightColor, Is.EqualTo(Color.White));

            foreach (var enemy in InDomain<MetaDataComponent>(entities, map).Where(uid =>
                         entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID is
                             "BitrunMilsimInfantry" or "BitrunMilsimRifleman").ToArray())
                entities.DeleteEntity(enemy);

            stage = InDomain<BitrunScenarioMarkerComponent>(entities, map).Single(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityName == "ancient_stage");
            var xform = entities.System<SharedTransformSystem>();
            xform.SetCoordinates(avatar, entities.GetComponent<TransformComponent>(stage).Coordinates);
            entities.EventBus.RaiseLocalEvent(stage, new InteractHandEvent(avatar, stage));
        });

        await pair.RunTicksSync(5 * pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            var cache = InDomain<MetaDataComponent>(entities, map).Single(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunEncryptedCache");
            var goal = InDomain<BitrunCacheGoalComponent>(entities, map).First();
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.Zero);
            entities.AddComponent<ApcPowerReceiverComponent>(receiver);
            var transform = entities.System<SharedTransformSystem>();
            transform.SetCoordinates(avatar, entities.GetComponent<TransformComponent>(cache).Coordinates);
            Assert.That(entities.System<PullingSystem>().TryStartPull(avatar, cache), Is.True);
            var goalLocation = entities.GetComponent<TransformComponent>(goal).Coordinates;
            transform.SetCoordinates(avatar, goalLocation);
            transform.SetCoordinates(cache, goalLocation);
        });

        await pair.RunTicksSync(2 * pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.Zero,
                "An unpowered research receiver must keep the reward pending.");
            Assert.That(InDomain<MetaDataComponent>(entities, outside.MapUid).Count(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunDecryptedCache"), Is.EqualTo(1));
            entities.RemoveComponent<ApcPowerReceiverComponent>(receiver);
        });

        await pair.RunTicksSync(2 * pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(150_000));
            Assert.That(entities.GetComponent<ResearchServerComponent>(replacement).Points, Is.Zero);
            Assert.That(entities.System<BitrunSystem>().GetPersonalResearchPoints(session.UserId), Is.EqualTo(150_000));
            Assert.That(InDomain<MetaDataComponent>(entities, map).Count(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunEncryptedCache"), Is.Zero);

            var exit = InDomain<BitrunExitComponent>(entities, map).First();
            entities.System<SharedTransformSystem>().SetCoordinates(avatar, entities.GetComponent<TransformComponent>(exit).Coordinates);
            entities.EventBus.RaiseLocalEvent(exit, new InteractHandEvent(avatar, exit));
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            Assert.That(entities.HasComponent<BitrunBodyComponent>(body), Is.False);
            Send(console, BitrunUiAction.Stop, match: matchId);
            Assert.That(entities.EntityExists(map), Is.False);
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(150_000));
            Assert.That(entities.GetComponent<ResearchServerComponent>(replacement).Points, Is.Zero);
            Assert.That(entities.System<BitrunSystem>().GetPersonalResearchPoints(session.UserId), Is.EqualTo(150_000));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
        });
        await pair.CleanReturnAsync();

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
            AdvanceTo(pair, match.Deadline);

            EntityUid SpawnOnline(string prototype, Vector2 offset)
            {
                var uid = entities.SpawnEntity(prototype, origin.Offset(offset));
                if (entities.HasComponent<ApcPowerReceiverComponent>(uid))
                    entities.RemoveComponent<ApcPowerReceiverComponent>(uid);
                return uid;
            }
        });

        await pair.RunTicksSync(3);
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
            Assert.That(bitrun.GetPersonalResearchPoints(players[1].UserId), Is.EqualTo(75_000));
            Assert.That(bitrun.GetPersonalResearchPoints(players[2].UserId), Is.EqualTo(75_000));
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(150_000));
            Assert.That(InDomain<MetaDataComponent>(entities, outside.MapUid).Any(uid =>
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "BitrunDecryptedCache"), Is.False);
        });

        await pair.RunTicksSync(pair.Server.Timing.TickRate + 1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ResearchServerComponent>(receiver).Points, Is.EqualTo(150_000));
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
