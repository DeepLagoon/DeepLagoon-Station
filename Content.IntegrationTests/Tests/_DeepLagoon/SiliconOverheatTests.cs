using System.Linq;
using Content.Server._Goobstation.Temperature;
using Content.Server.Temperature.Systems;
using Content.Shared.Atmos;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SiliconOverheatTests
{
    [TestCase("MobIPC")]
    [TestCase("MobSynth")]
    public async Task OverheatHalvesMovementUntilCooling(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        var server = pair.Server.EntMan;
        var temperature = server.System<TemperatureSystem>();
        var overheat = server.System<SlowOnOverheatSystem>();
        var threshold = Atmospherics.T0C + 110f;
        EntityUid mob = default;
        NetEntity netMob = default;
        float walk = 0f, sprint = 0f;

        await pair.Server.WaitAssertion(() =>
        {
            mob = server.SpawnEntity(prototype, map.GridCoords);
            netMob = server.GetNetEntity(mob);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), mob);

            server.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(mob);
            var movement = server.GetComponent<MovementSpeedModifierComponent>(mob);
            walk = movement.CurrentWalkSpeed;
            sprint = movement.CurrentSprintSpeed;
            Assert.That(walk, Is.Positive);
            Assert.That(sprint, Is.Positive);

            temperature.ForceChangeTemperature(mob, threshold - 1f);
            overheat.Update(0f);
            Assert.That(movement.CurrentWalkSpeed, Is.EqualTo(walk));
            Assert.That(movement.CurrentSprintSpeed, Is.EqualTo(sprint));

            temperature.ForceChangeTemperature(mob, threshold);
            overheat.Update(0f);
            Assert.That(server.GetComponent<MobStateComponent>(mob).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(movement.CurrentWalkSpeed, Is.EqualTo(walk * 0.5f));
            Assert.That(movement.CurrentSprintSpeed, Is.EqualTo(sprint * 0.5f));

            temperature.ForceChangeTemperature(mob, threshold + 100f);
        });
        await pair.RunTicksSync(15);
        await AssertClientSpeed(0.5f);

        await pair.Server.WaitAssertion(() => temperature.ForceChangeTemperature(mob, Atmospherics.T20C));
        await pair.RunTicksSync(15);
        await AssertClientSpeed(1f);
        await pair.Server.WaitAssertion(() =>
        {
            var movement = server.GetComponent<MovementSpeedModifierComponent>(mob);
            Assert.That(server.GetComponent<MobStateComponent>(mob).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(movement.CurrentWalkSpeed, Is.EqualTo(walk));
            Assert.That(movement.CurrentSprintSpeed, Is.EqualTo(sprint));
        });

        await pair.CleanReturnAsync();

        Task AssertClientSpeed(float multiplier) => pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var uid = client.GetEntity(netMob);
            var movement = client.GetComponent<MovementSpeedModifierComponent>(uid);
            Assert.That(movement.CurrentWalkSpeed, Is.EqualTo(walk * multiplier));
            Assert.That(movement.CurrentSprintSpeed, Is.EqualTo(sprint * multiplier));

            client.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(uid);
            Assert.That(movement.CurrentWalkSpeed, Is.EqualTo(walk * multiplier));
            Assert.That(movement.CurrentSprintSpeed, Is.EqualTo(sprint * multiplier));
        });
    }
}
