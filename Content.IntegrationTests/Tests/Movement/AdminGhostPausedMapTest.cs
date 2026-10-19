using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Administration.Managers;
using Content.Shared.Ghost;
using Content.Shared.Movement.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class AdminGhostPausedMapTest : InteractionTest
{
    protected override string PlayerPrototype => "AdminObserver";

    [TestCase(true, "AdminObserver", true, false)]
    [TestCase(false, "AdminObserver", true, false)]
    [TestCase(true, "MobObserver", true, false)]
    [TestCase(false, "MobObserver", true, false)]
    [TestCase(true, "MobObserver", false, false)]
    [TestCase(false, "MobObserver", false, false)]
    [TestCase(true, "AdminObserver", true, true)]
    [TestCase(false, "AdminObserver", true, true)]
    [TestCase(true, "MobObserver", true, true)]
    [TestCase(false, "MobObserver", true, true)]
    [TestCase(true, "MobObserver", false, true)]
    [TestCase(false, "MobObserver", false, true)]
    public async Task CanEnterAndMoveOnPausedMap(bool initialized, string prototype, bool admin, bool spawnOnMap)
    {
        EntityUid map = default;
        await Server.WaitPost(() =>
        {
            var admins = Server.ResolveDependency<IAdminManager>();
            admins.PromoteHost(ServerSession);
            if (!admin)
                admins.DeAdmin(ServerSession);

            // Created independently of the player's mapping command/session.
            map = MapSystem.CreateMap(runMapInit: initialized);
            MapSystem.SetPaused(map, true);
            var destination = new EntityCoordinates(map, Vector2.Zero);
            if (prototype != PlayerPrototype || spawnOnMap)
            {
                var old = SPlayer;
                SPlayer = SEntMan.SpawnEntity(prototype,
                    spawnOnMap ? destination : SEntMan.GetCoordinates(PlayerCoords));
                Player = SEntMan.GetNetEntity(SPlayer);
                Server.PlayerMan.SetAttachedEntity(ServerSession, SPlayer);
                SEntMan.DeleteEntity(old);
            }
            Transform.SetCoordinates(SPlayer, destination);
        });
        await RunTicks(10);
        CPlayer = ToClient(Player);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<PhysicsComponent>(CPlayer).IgnorePaused, Is.EqualTo(admin));
            if (admin)
            {
                var speed = CEntMan.GetComponent<MovementSpeedModifierComponent>(CPlayer);
                Assert.That(speed.Acceleration, Is.GreaterThan(0f));
                Assert.That(speed.WeightlessAcceleration, Is.GreaterThan(0f));
                Assert.That(speed.WeightlessSprintSpeed, Is.GreaterThan(0f));
            }
        });
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await RunSeconds(0.5f);
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
        await Server.WaitAssertion(() =>
        {
            if (admin)
                Assert.That(Transform.GetWorldPosition(SPlayer).X, Is.GreaterThan(1f));
            else
                Assert.That(Transform.GetWorldPosition(SPlayer).X, Is.EqualTo(0f).Within(0.01f));
            Assert.That(MapSystem.IsPaused(map), Is.True);
            Assert.That(MapSystem.IsInitialized(map), Is.EqualTo(initialized));
            if (prototype == "MobObserver")
                Assert.That(SEntMan.GetComponent<GhostComponent>(SPlayer).CanGhostInteract, Is.False);
        });
        if (admin && prototype == "MobObserver")
        {
            await Server.WaitPost(() => Server.ResolveDependency<IAdminManager>().DeAdmin(ServerSession));
            await RunTicks(5);
            await Server.WaitAssertion(() =>
                Assert.That(SEntMan.HasComponent<ActiveInputMoverComponent>(SPlayer), Is.False));
            await Client.WaitAssertion(() =>
                Assert.That(CEntMan.GetComponent<PhysicsComponent>(CPlayer).IgnorePaused, Is.False));
        }
        await Server.WaitPost(() =>
        {
            Transform.SetCoordinates(SPlayer, SEntMan.GetCoordinates(PlayerCoords));
            SEntMan.DeleteEntity(map);
        });
    }
}
