using Content.Server._DeepLagoon.InteractionPanel;
using Content.Server.Mood;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BlueMoonDependenceTests
{
    [Test]
    public async Task DependenceRequiresOverFortyUnitsProgressesThroughStagesAndCanBeDisabled()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            user = e.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            e.EnsureComponent<MoodComponent>(user);
            var physiology = e.GetComponent<BlueMoonPhysiologyComponent>(user);
            var system = e.System<InteractionPanelArousalSystem>();
            system.ApplyBlueMoonChemical(user, "Hexacrocin", 40);
            Assert.That(physiology.HexacrocinDependent, Is.False);
            physiology.NextChemicalEffect.Clear();
            system.ApplyBlueMoonChemical(user, "Hexacrocin", 41);
            Assert.That(physiology.HexacrocinDependent, Is.True);
            Assert.That(physiology.WithdrawalCycle, Is.EqualTo(-15));
            Assert.That(e.GetComponent<MoodComponent>(user).UncategorisedEffects["DLPhysiologyOverdose"], Is.EqualTo(-8));
        });
        for (var stage = 1; stage <= 4; stage++)
        {
            var expected = stage;
            await pair.Server.WaitPost(() =>
            {
                var state = pair.Server.EntMan.GetComponent<BlueMoonPhysiologyComponent>(user);
                state.WithdrawalCycle = (expected - 1) * 10;
                state.NextWithdrawal = TimeSpan.Zero;
            });
            await pair.RunTicksSync(pair.SecondsToTicks(2));
            await pair.Server.WaitAssertion(() =>
            {
                var mood = pair.Server.EntMan.GetComponent<MoodComponent>(user);
                Assert.That(mood.UncategorisedEffects["DLHexacrocinWithdrawal" + expected], Is.EqualTo(new[] { -2, -5, -8, -10 }[expected - 1]));
            });
        }
        await pair.Server.WaitPost(() =>
        {
            var e = pair.Server.EntMan;
            var prefs = e.GetComponent<InteractionPanelPreferencesComponent>(user);
            prefs.Profile = prefs.Profile.WithArousalPreferences(prefs.Profile.ArousalPreferences with { Aphrodisiacs = false });
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<BlueMoonPhysiologyComponent>(user).HexacrocinDependent, Is.False);
            Assert.That(e.GetComponent<MoodComponent>(user).UncategorisedEffects.Keys, Has.None.StartsWith("DLHexacrocinWithdrawal"));
            e.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }
}
