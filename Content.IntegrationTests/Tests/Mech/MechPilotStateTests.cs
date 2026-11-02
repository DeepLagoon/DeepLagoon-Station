using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Ghost;
using Content.Server.Mech.Systems;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Content.Shared.Ghost;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Mech;
using Content.Shared.Mech.Components;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;

namespace Content.IntegrationTests.Tests.Mech;

public sealed class MechPilotStateTests : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [TestCase(MobState.Critical)]
    [TestCase(MobState.Dead)]
    public async Task IncapacitatedPilotIsEjectedWithoutMechDamage(MobState state)
    {
        EntityUid mech = default;
        await Server.WaitAssertion(() =>
        {
            mech = EnterMech(openInterfaces: false);
        });
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.HasComponent<MechPilotComponent>(CPlayer), Is.True);
            Assert.That(CEntMan.HasComponent<RelayInputMoverComponent>(CPlayer), Is.True);
        });
        await Server.WaitAssertion(() =>
        {
            var comp = SEntMan.GetComponent<MechComponent>(mech);
            var integrity = comp.Integrity;
            OpenInterfaces(mech);

            // Reproduce death with a movement key still held by the mech.
#pragma warning disable RA0002
            SEntMan.GetComponent<InputMoverComponent>(mech).HeldMoveButtons = MoveButtons.Up;
#pragma warning restore RA0002
            SEntMan.System<MobStateSystem>().ChangeMobState(SPlayer, state);

            AssertReleased(mech);
            Assert.That(comp.Integrity, Is.EqualTo(integrity), "The pilot's state alone must trigger ejection.");
            Assert.That(SEntMan.GetComponent<MobStateComponent>(SPlayer).CurrentState, Is.EqualTo(state));

            // A second pilot must be able to take over the abandoned mech.
            var replacement = SEntMan.SpawnEntity("MobHuman", SEntMan.GetCoordinates(PlayerCoords));
            var system = SEntMan.System<MechSystem>();
            Assert.That(system.TryInsert(mech, replacement), Is.True);
            Assert.That(SEntMan.GetComponent<MovementRelayTargetComponent>(mech).Source, Is.EqualTo(replacement));
            Assert.That(system.TryEject(mech), Is.True);
            SEntMan.DeleteEntity(replacement);
            SEntMan.DeleteEntity(mech);
        });

        await RunTicks(5);
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<SharedHandsSystem>().EnumerateHeld(SPlayer), Is.Empty));
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.HasComponent<MechPilotComponent>(CPlayer), Is.False);
            Assert.That(CEntMan.HasComponent<RelayInputMoverComponent>(CPlayer), Is.False);
        });
    }

    [Test]
    public async Task DirectLethalHeatDamageEjectsPilot()
    {
        await Server.WaitAssertion(() =>
        {
            var mech = EnterMech();
            var comp = SEntMan.GetComponent<MechComponent>(mech);
            var integrity = comp.Integrity;
            var damage = SEntMan.System<DamageableSystem>();

            damage.TryChangeDamage(SPlayer, new DamageSpecifier { DamageDict = { ["Heat"] = 1 } });
            Assert.That(comp.PilotSlot.ContainedEntity, Is.EqualTo(SPlayer), "Nonlethal heat must not eject a healthy pilot.");
            damage.TryChangeDamage(SPlayer, new DamageSpecifier { DamageDict = { ["Heat"] = 1000 } });

            Assert.That(SEntMan.GetComponent<MobStateComponent>(SPlayer).CurrentState, Is.EqualTo(MobState.Dead));
            AssertReleased(mech);
            Assert.That(comp.Integrity, Is.EqualTo(integrity));
            SEntMan.DeleteEntity(mech);
        });
    }

    [Test]
    public async Task VoluntaryExitAlsoClosesPilotInterfaces()
    {
        await Server.WaitAssertion(() =>
        {
            var mech = EnterMech();
            Assert.That(SEntMan.System<MechSystem>().TryEject(mech), Is.True);
            AssertReleased(mech);
            Assert.That(SEntMan.GetComponent<MobStateComponent>(SPlayer).CurrentState, Is.EqualTo(MobState.Alive));
            SEntMan.DeleteEntity(mech);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<SharedHandsSystem>().EnumerateHeld(SPlayer), Is.Empty));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeadPilotCanObserveAndReturn(bool viaCommand)
    {
        await Server.WaitAssertion(() =>
        {
            var minds = SEntMan.System<SharedMindSystem>();
            var mind = minds.CreateMind(ServerSession.UserId);
            minds.TransferTo(mind.Owner, SPlayer);
            var mech = EnterMech();
            SEntMan.System<DamageableSystem>().TryChangeDamage(SPlayer,
                new DamageSpecifier { DamageDict = { ["Heat"] = 1000 } });
            AssertReleased(mech);
            if (viaCommand)
                Assert.That(SEntMan.System<GhostSystem>().OnGhostAttempt(mind.Owner, true, viaCommand: true), Is.True);
            SEntMan.DeleteEntity(mech);
        });
        await RunTicks(5);
        if (!viaCommand)
        {
            await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
            await RunTicks(5);
            await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
        }
        await Server.WaitAssertion(() =>
        {
            Assert.That(ServerSession.AttachedEntity, Is.Not.Null.And.Not.EqualTo(SPlayer));
            var ghost = ServerSession.AttachedEntity.Value;
            Assert.That(SEntMan.GetComponent<GhostComponent>(ghost).CanReturnToBody, Is.True);
            SEntMan.System<SharedMindSystem>().UnVisit(ServerSession);
            Assert.That(ServerSession.AttachedEntity, Is.EqualTo(SPlayer));
        });
    }

    private EntityUid EnterMech(bool openInterfaces = true)
    {
        var mech = SEntMan.SpawnEntity("MechRipleyBattery", SEntMan.GetCoordinates(PlayerCoords));
        Assert.That(SEntMan.System<MechSystem>().TryInsert(mech, SPlayer), Is.True);
        Assert.That(SEntMan.GetComponent<RelayInputMoverComponent>(SPlayer).RelayEntity, Is.EqualTo(mech));
        Assert.That(SEntMan.HasComponent<InteractionRelayComponent>(SPlayer), Is.True);
        Assert.That(SEntMan.System<SharedActionsSystem>().GetActions(SPlayer).Any(a => a.Comp.Container == mech), Is.True);
        Assert.That(SEntMan.System<SharedHandsSystem>().EnumerateHeld(SPlayer).Any(), Is.True);
        if (openInterfaces)
            OpenInterfaces(mech);
        return mech;
    }

    private void OpenInterfaces(EntityUid mech)
    {
        var ui = SEntMan.System<SharedUserInterfaceSystem>();
        Assert.That(ui.TryOpenUi(mech, MechUiKey.Key, SPlayer), Is.True);
        Assert.That(ui.IsUiOpen(mech, MechUiKey.Key, SPlayer), Is.True);
        Assert.That(ui.TryOpenUi(mech, RadarConsoleUiKey.Key, SPlayer), Is.True);
        Assert.That(ui.IsUiOpen(mech, RadarConsoleUiKey.Key, SPlayer), Is.True);
    }

    private void AssertReleased(EntityUid mech)
    {
        Assert.That(SEntMan.GetComponent<MechComponent>(mech).PilotSlot.ContainedEntity, Is.Null);
        Assert.That(SEntMan.HasComponent<MechPilotComponent>(SPlayer), Is.False);
        Assert.That(SEntMan.HasComponent<RelayInputMoverComponent>(SPlayer), Is.False);
        Assert.That(SEntMan.HasComponent<InteractionRelayComponent>(SPlayer), Is.False);
        Assert.That(SEntMan.HasComponent<MovementRelayTargetComponent>(mech), Is.False);
        Assert.That(SEntMan.GetComponent<InputMoverComponent>(mech).HeldMoveButtons, Is.EqualTo(MoveButtons.None));
        Assert.That(SEntMan.System<SharedActionsSystem>().GetActions(SPlayer).Any(a => a.Comp.Container == mech), Is.False);
        Assert.That(SEntMan.System<SharedUserInterfaceSystem>().IsUiOpen(mech, MechUiKey.Key, SPlayer), Is.False);
        Assert.That(SEntMan.System<SharedUserInterfaceSystem>().IsUiOpen(mech, RadarConsoleUiKey.Key, SPlayer), Is.False);
    }
}
