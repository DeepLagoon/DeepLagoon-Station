using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Mind;
using Content.Server.Power.Components;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.DragDrop;
using Content.Shared.Movement.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunLobbyTests
{
    [Test]
    public async Task EmptyLobbyUsesTheNextSlotAndDeletingAQueuedPodReleasesItsOccupant()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var outside = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var player = pair.Server.PlayerMan.Sessions.Single();
        EntityUid body = default;
        EntityUid pod = default;
        EntityUid console = default;
        EntityUid server = default;
        BitrunSessionEntry firstSlot = null!;

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId);
            maps.SetTiles(outside.Grid, outside.Grid.Comp, Enumerable.Range(-2, 6)
                .SelectMany(x => Enumerable.Range(-2, 6).Select(y => (new Vector2i(x, y), floor))).ToList());

            var origin = outside.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            body = entities.SpawnEntity("MobHuman", origin.Offset(-Vector2.UnitX));
            console = SpawnOnline("BitrunQuantumConsole", origin);
            server = SpawnOnline("BitrunQuantumServer", origin.Offset(Vector2.UnitX));
            pod = SpawnOnline("BitrunNetpod", origin.Offset(new Vector2(-1, 1)));
            var receiver = SpawnOnline("ResearchAndDevelopmentServer", origin.Offset(new Vector2(2, 0)));
            entities.GetComponent<BitrunServerComponent>(server).Credits = 3;

            var minds = entities.System<MindSystem>();
            var mind = minds.GetOrCreateMind(player.UserId);
            minds.TransferTo(mind.Owner, body);

            Send(console, new BitrunUiMessage(BitrunUiAction.BindResearch, target: entities.GetNetEntity(receiver)));
            Send(console, new BitrunUiMessage(BitrunUiAction.Start, "ancient_milsim"));
            firstSlot = ReadSession(console);
            Assert.That(firstSlot.Waiting, Is.True);

            var timing = pair.Server.Timing;
            timing.TimeBase = (firstSlot.Deadline, timing.CurTick);
        });

        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() =>
        {
            var nextSlot = ReadSession(console);
            Assert.Multiple(() =>
            {
                Assert.That(nextSlot.Waiting, Is.True);
                Assert.That(nextSlot.Id, Is.EqualTo(firstSlot.Id));
                Assert.That(nextSlot.Deadline - firstSlot.Deadline, Is.EqualTo(TimeSpan.FromMinutes(3)));
                Assert.That(entities.GetComponent<BitrunServerComponent>(server).Credits, Is.EqualTo(3));
            });

            var ui = entities.System<SharedUserInterfaceSystem>();
            var drop = new DragDropTargetEvent(body, body);
            entities.EventBus.RaiseLocalEvent(pod, ref drop);
            Assert.That(drop.Handled, Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            Assert.That(ui.IsUiOpen(pod, BitrunUiKey.Key, body), Is.True);
            Assert.That(ui.TryGetUiState<BitrunUiState>(pod, BitrunUiKey.Key, out var state), Is.True);
            Assert.That(state!.PreparingBody, Is.EqualTo(entities.GetNetEntity(body)));
            Assert.That(ReadSession(console).Attackers, Is.Zero, "Seating does not pick a side.");

            var movement = new ContainerRelayMovementEntityEvent(body);
            entities.EventBus.RaiseLocalEvent(pod, ref movement);
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(outside.Grid.Owner));
            Assert.That(ui.IsUiOpen(pod, BitrunUiKey.Key, body), Is.False);

            drop = new DragDropTargetEvent(body, body);
            entities.EventBus.RaiseLocalEvent(pod, ref drop);
            Assert.That(drop.Handled, Is.True);
            Send(pod, new BitrunUiMessage(BitrunUiAction.Enter, target: nextSlot.Server, side: BitrunSide.Attackers,
                session: nextSlot.Id));
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            Assert.That(ReadSession(console).Attackers, Is.EqualTo(1));

            entities.EventBus.RaiseLocalEvent(pod, ref movement);
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(outside.Grid.Owner));
            Assert.That(ReadSession(console).Attackers, Is.Zero);

            drop = new DragDropTargetEvent(body, body);
            entities.EventBus.RaiseLocalEvent(pod, ref drop);
            Assert.That(drop.Handled, Is.True);
            Send(pod, new BitrunUiMessage(BitrunUiAction.Enter, target: nextSlot.Server, side: BitrunSide.Attackers,
                session: nextSlot.Id));
            Assert.That(ReadSession(console).Attackers, Is.EqualTo(1));
            pair.Server.CfgMan.SetCVar(BitrunCVars.LobbyIntervalSeconds, 15);
            var debugSlot = ReadSession(console);
            Assert.Multiple(() =>
            {
                Assert.That(debugSlot.Deadline - pair.Server.Timing.CurTime, Is.EqualTo(TimeSpan.FromSeconds(15)));
                Assert.That(debugSlot.Id, Is.EqualTo(nextSlot.Id));
                Assert.That(debugSlot.Attackers, Is.EqualTo(1));
            });
            entities.DeleteEntity(pod);
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(body), Is.True);
                Assert.That(player.AttachedEntity, Is.EqualTo(body));
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(outside.Grid.Owner));
                Assert.That(ReadSession(console).Attackers, Is.Zero);
            });

            pair.Server.PlayerMan.SetAttachedEntity(player, null);
        });
        await pair.CleanReturnAsync();

        EntityUid SpawnOnline(string prototype, EntityCoordinates coordinates)
        {
            var uid = entities.SpawnEntity(prototype, coordinates);
            if (entities.HasComponent<ApcPowerReceiverComponent>(uid))
                entities.RemoveComponent<ApcPowerReceiverComponent>(uid);
            return uid;
        }

        void Send(EntityUid device, BitrunUiMessage message)
        {
            message.Actor = body;
            message.UiKey = BitrunUiKey.Key;
            entities.EventBus.RaiseLocalEvent(device, message);
        }

        BitrunSessionEntry ReadSession(EntityUid device)
        {
            entities.EventBus.RaiseLocalEvent(device, new BoundUIOpenedEvent(BitrunUiKey.Key, device, body));
            Assert.That(entities.System<SharedUserInterfaceSystem>().TryGetUiState<BitrunUiState>(device, BitrunUiKey.Key,
                out var state), Is.True);
            return state!.Sessions.Single();
        }
    }
}
