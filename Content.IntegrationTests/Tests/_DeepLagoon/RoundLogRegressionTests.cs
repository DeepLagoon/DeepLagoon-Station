using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Abilities.Psionics;
using Content.Server.Explosion.Components;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit;
using Content.Shared.Psionics;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class RoundLogRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FrozenProximityTriggerPreservesInactiveVisualDeadline(bool nearMaximum)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid flasher = default;
        TimeSpan pausedAt = default, triggerAt = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            flasher = entities.SpawnEntity("PortableFlasher", map.GridCoords);
            var timing = pair.Server.ResolveDependency<IGameTiming>();
            pausedAt = timing.CurTime;
            triggerAt = pausedAt + TimeSpan.FromSeconds(30);
            var component = entities.GetComponent<TriggerOnProximityComponent>(flasher);
            component.NextTrigger = triggerAt;
            component.NextVisualUpdate = nearMaximum ? TimeSpan.MaxValue - TimeSpan.FromMilliseconds(1) : TimeSpan.MaxValue;
            entities.System<MetaDataSystem>().SetEntityPaused(flasher, true);
        });
        await pair.Server.WaitRunTicks(15);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var duration = pair.Server.ResolveDependency<IGameTiming>().CurTime - pausedAt;
            Assert.That(duration, Is.GreaterThan(TimeSpan.FromMilliseconds(1)));
            entities.System<MetaDataSystem>().SetEntityPaused(flasher, false);
            var component = entities.GetComponent<TriggerOnProximityComponent>(flasher);
            Assert.That(component.NextTrigger, Is.EqualTo(triggerAt + duration));
            Assert.That(component.NextVisualUpdate, Is.EqualTo(TimeSpan.MaxValue));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReloadedPowerDoesNotDuplicateActionsAndCanBeRemoved()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var mob = entities.SpawnEntity("MobHuman", map.GridCoords);
            var abilities = entities.System<PsionicAbilitiesSystem>();
            var power = pair.Server.ResolveDependency<IPrototypeManager>().Index<PsionicPowerPrototype>("PyrokineticFlare");
            var psionic = entities.EnsureComponent<PsionicComponent>(mob);
            abilities.InitializePsionicPower(mob, power, psionic, false);
            var count = psionic.ActivePowers.Count;
            var action = psionic.Actions[power.ID];
            var slots = psionic.PowerSlotsTaken;
            var reloaded = new PsionicPowerPrototype();
            typeof(PsionicPowerPrototype).GetProperty(nameof(PsionicPowerPrototype.ID))!.SetValue(reloaded, power.ID);
            typeof(PsionicPowerPrototype).GetProperty(nameof(PsionicPowerPrototype.InitializeFunctions))!.SetValue(reloaded, power.InitializeFunctions);
            typeof(PsionicPowerPrototype).GetProperty(nameof(PsionicPowerPrototype.RemovalFunctions))!.SetValue(reloaded, power.RemovalFunctions);
            abilities.InitializePsionicPower(mob, reloaded, psionic, false);
            Assert.That(psionic.ActivePowers.Count, Is.EqualTo(count));
            Assert.That(psionic.Actions[power.ID], Is.EqualTo(action));
            Assert.That(psionic.PowerSlotsTaken, Is.EqualTo(slots));
            abilities.RemovePsionicPower(mob, reloaded, forced: true);
            Assert.That(psionic.ActivePowers.Any(p => p.ID == power.ID), Is.False);
            Assert.That(psionic.Actions.ContainsKey(power.ID), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DisposalWithoutOptionalDeviceLinkCanBecomeReady()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var unit = entities.SpawnEntity("DisposalUnit", map.GridCoords);
            entities.RemoveComponent<Content.Shared.DeviceLinking.DeviceLinkSourceComponent>(unit);
            var component = entities.GetComponent<DisposalUnitComponent>(unit);
            component.State = DisposalsPressureState.Pressurizing;
            var system = entities.System<SharedDisposalUnitSystem>();
            typeof(SharedDisposalUnitSystem).GetMethod("UpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(system, [unit, DisposalsPressureState.Ready, component, entities.GetComponent<MetaDataComponent>(unit)]);
            Assert.That(component.State, Is.EqualTo(DisposalsPressureState.Ready));
        });
        await pair.CleanReturnAsync();
    }
}
