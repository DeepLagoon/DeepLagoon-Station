using Content.Server._DeepLagoon.BodyCustomization;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Humanoid;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class GenitalArousalTests
{
    [Test]
    public async Task AdjustmentsRespectStrictThresholdAndAnatomy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var arousal = entities.System<InteractionPanelArousalSystem>();
            foreach (var (amount, initial, expected) in new[]
                     {
                         (19f, false, false), (20f, false, false), (21f, false, true),
                         (19f, true, false), (20f, true, true), (21f, true, true),
                     })
            {
                var uid = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
                var appearance = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var feature = BodyCustomizationCatalog.Default("penis");
                feature.Enabled = true;
                appearance.BodyFeatures = [feature];
                appearance.GenitalAroused = initial;
                var maximum = arousal.Maximum(uid);
                arousal.AdjustArousal(uid, uid, maximum * amount / 100);
                Assert.That(appearance.GenitalAroused, Is.EqualTo(expected), $"{amount}%, initial={initial}");
                entities.DeleteEntity(uid);
            }

            foreach (var includeDisabled in new[] { false, true })
            {
                var uid = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
                var appearance = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var feature = BodyCustomizationCatalog.Default("breasts");
                feature.Enabled = true;
                appearance.BodyFeatures = [feature];
                if (includeDisabled)
                {
                    var disabled = BodyCustomizationCatalog.Default("penis");
                    disabled.Enabled = false;
                    appearance.BodyFeatures.Add(disabled);
                }
                arousal.AdjustArousal(uid, uid, arousal.Maximum(uid) * .5f);
                Assert.That(appearance.GenitalAroused, Is.False, "Missing or disabled anatomy is unchanged");
                Assert.That(entities.HasComponent<GenitalArousalCooldownComponent>(uid), Is.False);
                entities.DeleteEntity(uid);
            }

            var scaled = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var scaledAppearance = entities.GetComponent<HumanoidAppearanceComponent>(scaled);
            var penis = BodyCustomizationCatalog.Default("penis");
            penis.Enabled = true;
            scaledAppearance.BodyFeatures = [penis];
            var body = entities.System<BodyCustomizationSystem>();
            body.UpdateGenitalArousal(scaled, 40, 200);
            Assert.That(scaledAppearance.GenitalAroused, Is.False, "Threshold is relative to capacity");
            body.UpdateGenitalArousal(scaled, 41, 200);
            Assert.That(scaledAppearance.GenitalAroused, Is.True);
            entities.DeleteEntity(scaled);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CooldownDefersReversalAndRechecksCurrentLevelWithoutAnotherAdjustment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(user);
            var feature = BodyCustomizationCatalog.Default("penis");
            feature.Enabled = true;
            appearance.BodyFeatures = [feature];
            var system = entities.System<InteractionPanelArousalSystem>();
            var maximum = system.Maximum(user);
            system.AdjustArousal(user, user, maximum * .5f);
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            var cooldown = entities.GetComponent<GenitalArousalCooldownComponent>(user);
            Assert.That(appearance.GenitalAroused, Is.True);
            Assert.That(cooldown.NextAutomaticChange, Is.EqualTo(now + TimeSpan.FromSeconds(30)));
            system.AdjustArousal(user, user, -maximum);
            Assert.That(appearance.GenitalAroused, Is.True, "Reversal waits for the cooldown");
            system.AdjustArousal(user, user, maximum * .5f);
            Assert.That(cooldown.NextAutomaticChange, Is.EqualTo(now + TimeSpan.FromSeconds(30)),
                "Unchanged state does not restart the cooldown");
        });

        await pair.RunTicksSync(pair.SecondsToTicks(1));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(user).GenitalAroused, Is.True);
            var system = entities.System<InteractionPanelArousalSystem>();
            system.AdjustArousal(user, user, -system.Maximum(user));
        });
        await pair.RunTicksSync(pair.SecondsToTicks(28));
        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).GenitalAroused,
                Is.True, "The inactive state is still deferred before 30 seconds"));
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(user);
            Assert.That(appearance.GenitalAroused, Is.False, "Periodic update applies the deferred reversal");
            var system = entities.System<InteractionPanelArousalSystem>();
            system.AdjustArousal(user, user, system.Maximum(user) * .8f);
            Assert.That(appearance.GenitalAroused, Is.False, "Cooldown also applies after switching off");
        });
        await pair.RunTicksSync(pair.SecondsToTicks(31));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(user).GenitalAroused, Is.True,
                "Pending activation uses the current level after cooldown");
            entities.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DecayAndDischargeRestoreTheInactiveState()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(user);
            var feature = BodyCustomizationCatalog.Default("penis");
            feature.Enabled = true;
            appearance.BodyFeatures = [feature];
            appearance.GenitalAroused = true;
            var arousal = entities.GetComponent<InteractionPanelArousalComponent>(user);
            arousal.Current = arousal.Maximum * .2f;
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(user);
            Assert.That(appearance.GenitalAroused, Is.False, "Natural decay below 20% switches off");
            appearance.GenitalAroused = true;
            entities.GetComponent<GenitalArousalCooldownComponent>(user).NextAutomaticChange = TimeSpan.Zero;
            var system = entities.System<InteractionPanelArousalSystem>();
            system.AdjustArousal(user, user, system.Maximum(user));
            Assert.That(entities.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.Zero);
            Assert.That(appearance.GenitalAroused, Is.False, "Discharge also respects the inactive threshold");
            entities.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }
}
