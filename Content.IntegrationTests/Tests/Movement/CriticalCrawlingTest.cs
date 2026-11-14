using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Standing;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class CriticalCrawlingTest : MovementTest
{
    [Test]
    public async Task HalfCriticalHealthAllowsCrawlingAndOneDosePerEpisode()
    {
        FixedPoint2 critical = default;
        FixedPoint2 half = default;
        float start = 0;
        await Server.WaitAssertion(() =>
        {
            var thresholds = SEntMan.System<MobThresholdSystem>();
            critical = thresholds.GetThresholdForState(SPlayer, MobState.Critical);
            var dead = thresholds.GetThresholdForState(SPlayer, MobState.Dead);
            half = critical + (dead - critical) / 2;
            var states = SEntMan.System<MobStateSystem>();
            var blocker = SEntMan.System<ActionBlockerSystem>();
            var standing = SEntMan.System<StandingStateSystem>();

            SetDamage(half + 1);
            Assert.That(states.IsCritical(SPlayer), Is.True);
            Assert.That(blocker.CanMove(SPlayer), Is.False);
            Assert.That(Adrenaline(), Is.EqualTo(FixedPoint2.Zero));

            SetDamage(half);
            Assert.That(standing.IsDown(SPlayer), Is.True);
            Assert.That(blocker.CanMove(SPlayer), Is.True);
            Assert.That(blocker.CanConsciouslyPerformAction(SPlayer), Is.False);
            Assert.That(standing.Stand(SPlayer), Is.False);
            Assert.That(Adrenaline(), Is.EqualTo(FixedPoint2.New(5)));

            SetDamage(half + 1);
            Assert.That(blocker.CanMove(SPlayer), Is.False);
            SetDamage(half - 1);
            Assert.That(blocker.CanMove(SPlayer), Is.True);
            Assert.That(Adrenaline(), Is.EqualTo(FixedPoint2.New(5)));
            start = Transform.GetWorldPosition(SPlayer).X;
        });

        await RunTicks(8);
        await Client.WaitAssertion(() =>
        {
            var blocker = CEntMan.System<ActionBlockerSystem>();
            Assert.That(blocker.UpdateCanMove(CPlayer), Is.True);
            Assert.That(CEntMan.System<StandingStateSystem>().IsDown(CPlayer), Is.True);
        });

        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await RunTicks(20);
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Transform.GetWorldPosition(SPlayer).X, Is.GreaterThan(start));
            Assert.That(SEntMan.System<StandingStateSystem>().IsDown(SPlayer), Is.True);

            SetDamage(FixedPoint2.Zero);
            Assert.That(SEntMan.System<MobStateSystem>().IsAlive(SPlayer), Is.True);
            Assert.That(SEntMan.HasComponent<CriticalCrawlingComponent>(SPlayer), Is.False);

            var remainingDose = Adrenaline();
            SetDamage(half);
            Assert.That(Adrenaline(), Is.EqualTo(remainingDose + 5));
            SetDamage(critical);
            Assert.That(SEntMan.System<MobStateSystem>().IsCritical(SPlayer), Is.True);
        });

        await RunSeconds(2.5f);
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<MobStateSystem>().IsAlive(SPlayer), Is.True,
            "The adrenaline dose should bring mild critical injuries below the threshold through normal metabolism."));
    }

    private FixedPoint2 Adrenaline() =>
        SEntMan.System<SharedSolutionContainerSystem>().GetTotalPrototypeQuantity(SPlayer, "Epinephrine");

    private void SetDamage(FixedPoint2 amount)
    {
        var damage = new DamageSpecifier();
        if (amount > 0)
            damage.DamageDict.Add("Blunt", amount);

        var damageable = SEntMan.GetComponent<DamageableComponent>(SPlayer);
        SEntMan.System<DamageableSystem>().SetDamage(SPlayer, damageable, damage);
    }
}
