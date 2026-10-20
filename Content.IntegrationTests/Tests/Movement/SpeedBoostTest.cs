using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Input;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.Input;
using Robust.Shared.Maths;
using Robust.Client.Input;
using Content.Client.UserInterface.Systems.Chat;
using Robust.Client.UserInterface;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class SpeedBoostTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [Test]
    public async Task SprintFeedbackAppearsOncePerTransitionInPlayerChat()
    {
        string started = null;
        string stopped = null;
        string exhausted = null;
        await Server.WaitPost(() =>
        {
            var loc = Server.ResolveDependency<ILocalizationManager>();
            started = loc.GetString("speed-boost-started");
            stopped = loc.GetString("speed-boost-stopped");
            exhausted = loc.GetString("speed-boost-exhausted");
        });
        var chat = Client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>();
        await Client.WaitPost(() => chat.History.Clear());

        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Down);
        await RunTicks(5);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x => x.Msg.Message == started), Is.Zero));
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x => x.Msg.Message == started), Is.EqualTo(1)));
        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Up);
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(chat.History.Count(x => x.Msg.Message == stopped), Is.EqualTo(1)));

        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Down);
        await RunSeconds(13f);
        await Client.WaitAssertion(() =>
        {
            Assert.That(chat.History.Count(x => x.Msg.Message == started), Is.EqualTo(2));
            Assert.That(chat.History.Count(x => x.Msg.Message == exhausted), Is.EqualTo(1));
            Assert.That(chat.History.Count(x => x.Msg.Message == stopped), Is.EqualTo(1));
        });
        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Up);
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
    }

    [Test]
    public async Task AltCombinationsPreserveHeldBoost()
    {
        await Client.WaitAssertion(() =>
        {
            var input = Client.ResolveDependency<IInputManager>();
            input.KeyDown(new KeyEventArgs(Keyboard.Key.Alt, false, true, false, false, false, 0));
            Assert.That(input.DownKeyFunctions.Contains(ContentKeyFunctions.DeepLagoonSpeedBoost), Is.True);
            foreach (var key in new[] { Keyboard.Key.E, Keyboard.Key.Z, Keyboard.Key.V, Keyboard.Key.MouseLeft })
            {
                input.KeyDown(new KeyEventArgs(key, false, true, false, false, false, 0));
                Assert.That(input.DownKeyFunctions.Contains(ContentKeyFunctions.DeepLagoonSpeedBoost), Is.True, $"Alt + {key}");
                var plainAction = key switch
                {
                    Keyboard.Key.E => ContentKeyFunctions.ActivateItemInWorld,
                    Keyboard.Key.Z => ContentKeyFunctions.UseItemInHand,
                    Keyboard.Key.V => ContentKeyFunctions.OpenBackpack,
                    _ => EngineKeyFunctions.Use,
                };
                Assert.That(Client.System<Robust.Client.GameObjects.InputSystem>().CmdStates.GetState(plainAction),
                    Is.EqualTo(BoundKeyState.Up), $"Alt + {key} must not also activate {key}");
                input.KeyUp(new KeyEventArgs(key, false, true, false, false, false, 0));
            }
            input.KeyUp(new KeyEventArgs(Keyboard.Key.Alt, false, false, false, false, false, 0));
            Assert.That(input.DownKeyFunctions.Contains(ContentKeyFunctions.DeepLagoonSpeedBoost), Is.False);
        });
        await RunTicks(5);
    }

    [Test]
    public async Task TwelveSecondsExhaustsStaminaWithoutCombatCrit()
    {
        await Server.WaitAssertion(() =>
        {
            var stamina = SEntMan.GetComponent<StaminaComponent>(SPlayer);
            var system = Server.System<StaminaSystem>();
            var capacity = StaminaSystem.SpeedBoostCapacity(stamina);

            system.ConsumeSpeedBoostStamina((SPlayer, stamina), 11f);
            Assert.That(stamina.StaminaDamage, Is.EqualTo(capacity * 11f / 12f).Within(0.001f));
            Assert.That(system.CanSpeedBoost(stamina), Is.True);
            system.ConsumeSpeedBoostStamina((SPlayer, stamina), 1f);
            Assert.That(stamina.StaminaDamage, Is.EqualTo(capacity));
            Assert.That(stamina.SpeedBoostExhausted, Is.True);
            Assert.That(stamina.Critical, Is.False);
            Assert.That(system.CanSpeedBoost(stamina), Is.False);

            system.ConsumeSpeedBoostStamina((SPlayer, stamina), 10f);
            Assert.That(stamina.StaminaDamage, Is.EqualTo(capacity));
            system.RecoverSpeedBoost((SPlayer, stamina));
            Assert.That(stamina.SpeedBoostExhausted, Is.True);
            system.TakeStaminaDamage(SPlayer, -capacity * 0.21f, stamina, visual: false);
            system.RecoverSpeedBoost((SPlayer, stamina));
            Assert.That(stamina.SpeedBoostExhausted, Is.False);
        });
    }

    [TestCase("MobHuman", 1.35f)]
    [TestCase("AdminObserver", 2f)]
    [TestCase("MobObserver", 1f)]
    public async Task BoostIsTemporaryAndPreservesBaseSpeed(string prototype, float multiplier)
    {
        await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var mover = SEntMan.GetComponent<InputMoverComponent>(uid);
            var speed = SEntMan.GetComponent<MovementSpeedModifierComponent>(uid);
            var system = Server.System<SharedMoverController>();
            var baseSpeed = speed.BaseSprintSpeed;
            system.SetVelocityDirection((uid, mover), Direction.East, 0, true);
            var baseline = system.GetVelocityInput(mover).Sprinting.Length();

            system.SetSpeedBoost((uid, mover), 0, true);
            Assert.That(system.GetVelocityInput(mover).Sprinting.Length(), Is.EqualTo(baseline * multiplier).Within(0.0001f));
            system.SetSpeedBoost((uid, mover), 0, false);
            Assert.That(system.GetVelocityInput(mover).Sprinting.Length(), Is.EqualTo(baseline).Within(0.0001f));
            Assert.That(speed.BaseSprintSpeed, Is.EqualTo(baseSpeed));
            system.SetVelocityDirection((uid, mover), Direction.East, 0, false);
        });
    }

    [Test]
    public async Task BoostInputReachesServerAndReleases()
    {
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Down);
        await RunTicks(5);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<InputMoverComponent>(SPlayer).HeldMoveButtons.HasFlag(MoveButtons.SpeedBoost), Is.True));

        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Up);
        await RunTicks(5);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<InputMoverComponent>(SPlayer).HeldMoveButtons.HasFlag(MoveButtons.SpeedBoost), Is.False));
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
    }

    [Test]
    public async Task HoldingAltExhaustsAfterTwelveSecondsAndCannotAutoRestart()
    {
        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Down);
        await RunSeconds(1f);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<StaminaComponent>(SPlayer).StaminaDamage, Is.Zero));

        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await RunSeconds(11f);
        await Server.WaitAssertion(() =>
        {
            var stamina = SEntMan.GetComponent<StaminaComponent>(SPlayer);
            Assert.That(stamina.SpeedBoostExhausted, Is.False);
            Assert.That(stamina.StaminaDamage, Is.InRange(stamina.CritThreshold * 0.90f, stamina.CritThreshold * 0.94f));
        });
        await RunSeconds(1.2f);
        await Server.WaitAssertion(() =>
        {
            var stamina = SEntMan.GetComponent<StaminaComponent>(SPlayer);
            Assert.That(stamina.SpeedBoostExhausted, Is.True);
            Assert.That(stamina.Critical, Is.False);
        });

        await RunSeconds(9f);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<StaminaComponent>(SPlayer).SpeedBoostExhausted, Is.True));
        await SetKey(ContentKeyFunctions.DeepLagoonSpeedBoost, BoundKeyState.Up);
        await RunTicks(5);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<StaminaComponent>(SPlayer).SpeedBoostExhausted, Is.False));
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
    }
}
