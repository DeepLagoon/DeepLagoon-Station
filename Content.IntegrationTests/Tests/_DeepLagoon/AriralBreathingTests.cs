using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Chemistry.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class AriralBreathingTests
{
    [TestCase("MobAriral", true)]
    [TestCase("MobHuman", true)]
    [TestCase("MobVox", false)]
    public async Task OxygenBreathingRespectsSpecies(string prototype, bool breathesOxygen)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var mob = entities.SpawnEntity(prototype, map.GridCoords);
            var respirator = entities.System<RespiratorSystem>();
            Assert.That(respirator.CanMetabolizeGas(mob, StationAir()), Is.EqualTo(breathesOxygen));
            Assert.That(respirator.CanMetabolizeGas(mob, GasMixture.SpaceGas), Is.False);
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AriralDoesNotSuffocateInStationAir()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var entities = pair.Server.EntMan;
        EntityUid map = default;
        EntityUid mob = default;
        await pair.Server.WaitAssertion(() =>
        {
            map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            entities.System<AtmosphereSystem>().SetMapAtmosphere(map, false, StationAir());
            mob = entities.SpawnEntity("MobAriral", new MapCoordinates(0, 0, mapId));
        });

        // Longer than the initial oxygen reserve, through repeated real breathing cycles.
        await pair.RunTicksSync(1800);

        await pair.Server.WaitAssertion(() =>
        {
            var respirator = entities.GetComponent<RespiratorComponent>(mob);
            Assert.That(respirator.Saturation, Is.GreaterThanOrEqualTo(respirator.SuffocationThreshold));
            Assert.That(entities.GetComponent<DamageableComponent>(mob).Damage.DamageDict["Asphyxiation"].Float(), Is.Zero);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EmergencyMedipenTreatsAriralAsphyxiationInCriticalCondition()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        EntityUid mob = default;
        EntityUid pen = default;
        DamageableComponent damage = null;
        await pair.Server.WaitAssertion(() =>
        {
            mob = entities.SpawnEntity("MobAriral", map.GridCoords);
            pen = entities.SpawnEntity("EmergencyMedipen", map.GridCoords);
            entities.System<DamageableSystem>().TryChangeDamage(mob,
                new DamageSpecifier { DamageDict = new() { ["Asphyxiation"] = 110 } }, ignoreResistances: true);
            Assert.That(entities.System<MobStateSystem>().IsCritical(mob), Is.True);
            damage = entities.GetComponent<DamageableComponent>(mob);
            Assert.That(damage.Damage.DamageDict["Asphyxiation"].Float(), Is.EqualTo(110));
            Assert.That(entities.System<HypospraySystem>().TryDoInject(
                (pen, entities.GetComponent<HyposprayComponent>(pen)), mob, mob), Is.True);
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(pen, "pen", out _, out var contents), Is.True);
            Assert.That(contents.Volume.Float(), Is.Zero);

        });

        // Allow a medicine tick while the patient is still critical and cannot breathe unaided.
        await pair.RunTicksSync(90);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(damage.Damage.DamageDict["Asphyxiation"].Float(), Is.LessThan(110));
            entities.DeleteEntity(pen);
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    private static GasMixture StationAir()
    {
        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        moles[(int) Gas.Oxygen] = 21.82f;
        moles[(int) Gas.Nitrogen] = 82.10f;
        return new GasMixture(moles, Atmospherics.T20C);
    }
}
