using System;
using System.Linq;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Humanoid;
using Content.Server.Traits;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Implants.Components;
using Content.Shared.Fluids.Components;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BloodAndMedicalMinorFixTests
{
    [Test]
    public async Task BloodDeficiencyDestroysBloodWithoutSpillingWhileWoundsStillBleed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var patient = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.AddComponent<BloodDeficiencyComponent>(patient).BloodLossPercentage = .01f;
            var blood = entities.GetComponent<BloodstreamComponent>(patient);
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.ResolveSolution(patient, blood.BloodSolutionName, ref blood.BloodSolution, out var solution), Is.True);
            var before = solution.Volume;
#pragma warning disable RA0002 // Seed only the update timer so the actual scheduled bloodstream path runs now.
            blood.NextUpdate = pair.Server.ResolveDependency<IGameTiming>().CurTime;
#pragma warning restore RA0002
            entities.System<BloodstreamSystem>().Update(0);
            Assert.That(solution.Volume, Is.LessThan(before));
            Assert.That(solutions.ResolveSolution(patient, blood.BloodTemporarySolutionName, ref blood.TemporarySolution, out var temporary), Is.True);
            Assert.That(temporary.Volume.Float(), Is.Zero);
            Assert.That(entities.EntityQuery<PuddleComponent>().Any(), Is.False);
            Assert.That(blood.BleedAmount, Is.Zero);
            // The ordinary injury path still transfers blood into an actual puddle.
            entities.System<BloodstreamSystem>().TryModifyBloodLevel(patient, -10, blood);
            Assert.That(entities.EntityQuery<PuddleComponent>().Any(), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CharacterBloodColorTravelsWithDrawnBloodWithoutChangingOtherDonorsOrPrototype()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var normal = prototypes.Index<ReagentPrototype>("Blood").SubstanceColor;
            var patient = entities.SpawnEntity("MobHuman", map.GridCoords);
            var other = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.System<HumanoidAppearanceSystem>().LoadProfile(patient, new HumanoidCharacterProfile().WithBloodColor("#123456"));
            var blood = entities.GetComponent<BloodstreamComponent>(patient);
            var otherBlood = entities.GetComponent<BloodstreamComponent>(other);
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.ResolveSolution(patient, blood.BloodSolutionName, ref blood.BloodSolution, out var solution), Is.True);
            Assert.That(solutions.ResolveSolution(other, otherBlood.BloodSolutionName, ref otherBlood.BloodSolution, out var otherSolution), Is.True);
            var custom = Color.FromHex("#123456");
            Assert.That(solution.GetColor(prototypes), Is.EqualTo(custom));
            Assert.That(solution.GetColorWithOnly(prototypes, "Blood"), Is.EqualTo(custom));
            Assert.That(otherSolution.GetColor(prototypes), Is.EqualTo(normal));
            var drawn = solutions.SplitSolution(blood.BloodSolution!.Value, 5);
            Assert.That(drawn.GetColor(prototypes), Is.EqualTo(custom));
            entities.System<BloodstreamSystem>().SetBloodColor(patient, Color.Blue);
            Assert.That(solution.GetColor(prototypes), Is.EqualTo(Color.Blue));
            Assert.That(drawn.GetColor(prototypes), Is.EqualTo(custom), "Changing the body must not recolor an earlier sample.");
            var mixed = solutions.SplitSolution(otherBlood.BloodSolution!.Value, 5);
            mixed.AddSolution(drawn, prototypes);
            Assert.That(mixed.GetColor(prototypes), Is.EqualTo(Color.InterpolateBetween(normal, custom, .5f)));
            Assert.That(prototypes.Index<ReagentPrototype>("Blood").SubstanceColor, Is.EqualTo(normal));
            Assert.That(new DnaData { DNA = "test" }.Equals(new BloodColorData { Color = custom }), Is.False);
            entities.System<BloodstreamSystem>().SetBloodColor(patient, null);
            Assert.That(solution.GetColor(prototypes), Is.EqualTo(normal));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RattleDeduplicationSeparatesPatientsAlertTypesAndCooldownBoundaries()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<TriggerSystem>();
            var patient = entities.SpawnEntity("MobHuman", map.GridCoords);
            var other = entities.SpawnEntity("MobHuman", map.GridCoords);
            var implant = new RattleComponent { RadioChannel = "Medical" };
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "critical"), Is.True);
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "critical"), Is.False);
            Assert.That(system.ShouldSendRattleAlert(other, implant, "critical"), Is.True);
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "dead"), Is.True);
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "revived"), Is.True);
            var history = entities.GetComponent<RattleAlertCooldownComponent>(patient);
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            history.LastSent[("Medical", "critical")] = now - implant.RepeatDelay + TimeSpan.FromMilliseconds(1);
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "critical"), Is.False);
            history.LastSent[("Medical", "critical")] = now - implant.RepeatDelay;
            Assert.That(system.ShouldSendRattleAlert(patient, implant, "critical"), Is.True);
        });
        await pair.CleanReturnAsync();
    }
}
