using System.Collections.Generic;
using System.Linq;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Server.Chat.Systems;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ArousalPreferencesTests
{
    private sealed class MoanListener : EntitySystem
    {
        public readonly Dictionary<EntityUid, int> Counts = new();

        public override void Initialize() => SubscribeLocalEvent<InteractionPanelArousalComponent, EmoteEvent>(OnEmote);

        private void OnEmote(EntityUid uid, InteractionPanelArousalComponent component, ref EmoteEvent args)
        {
            if (args.Emote.ID == "ExtendedMoan") Counts[uid] = Counts.GetValueOrDefault(uid) + 1;
        }
    }

    [Test]
    public async Task ReplicatedPreferencesAffectOnlyTheAttachedCharacterAndAreClamped()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid other = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            other = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.GetComponent<InteractionPanelArousalComponent>(other).ArousalMultiplier = 50;
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
        });
        await pair.Client.WaitPost(() =>
        {
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.AutomaticMoans, false);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.MoanChance, 999f);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.ArousalMultiplier, 999f);
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var state = entities.GetComponent<InteractionPanelArousalComponent>(user);
            Assert.That(state.AutomaticMoans, Is.False);
            Assert.That(state.MoanChance, Is.EqualTo(100));
            Assert.That(state.ArousalMultiplier, Is.EqualTo(300));
            var system = entities.System<InteractionPanelArousalSystem>();
            system.AdjustArousal(other, user, 10);
            system.AdjustArousal(user, other, 10);
            Assert.That(state.Current, Is.EqualTo(30));
            Assert.That(entities.GetComponent<InteractionPanelArousalComponent>(other).Current, Is.EqualTo(5));
        });
        await pair.Client.WaitPost(() =>
        {
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.AutomaticMoans, true);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.MoanChance, 0f);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.ArousalMultiplier, 0f);
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var state = entities.GetComponent<InteractionPanelArousalComponent>(user);
            var before = state.Current;
            entities.System<InteractionPanelArousalSystem>().AdjustArousal(other, user, 10);
            Assert.That(state.Current, Is.EqualTo(before));
            Assert.That(state.AutomaticMoans, Is.True);
            Assert.That(state.MoanChance, Is.Zero);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            entities.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GainUsesRecipientsSettingsAndAllowsGainDuringRecovery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<InteractionPanelArousalSystem>();
            var source = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var target = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var sourceState = entities.GetComponent<InteractionPanelArousalComponent>(source);
            var state = entities.GetComponent<InteractionPanelArousalComponent>(target);
            sourceState.ArousalMultiplier = 0;
            state.ArousalMultiplier = 300;
            system.AdjustArousal(source, target, 10, interaction: true);
            Assert.That(state.Current, Is.EqualTo(30));
            Assert.That(sourceState.Current, Is.Zero);
            system.AdjustArousal(source, target, 0, interaction: true);
            Assert.That(state.Current, Is.EqualTo(30));
            system.AdjustArousal(source, target, 0);
            Assert.That(state.Current, Is.EqualTo(30), "A zero non-interaction adjustment remains inert");
            system.AdjustArousal(source, target, -10);
            Assert.That(state.Current, Is.EqualTo(20));
            state.ArousalMultiplier = 0;
            system.AdjustArousal(source, target, 50, interaction: true);
            Assert.That(state.Current, Is.EqualTo(20));
            state.ArousalMultiplier = 300;
            entities.EnsureComponent<InteractionPanelPreferencesComponent>(target).Profile = new HumanoidCharacterProfile()
                .WithArousalPreferences(new BlueMoonPreferences { Enabled = false });
            system.AdjustArousal(source, target, 10, interaction: true);
            Assert.That(state.Current, Is.EqualTo(20), "The character physiology switch blocks positive stimulation");
            Assert.That(state.Current, Is.EqualTo(20));
            entities.GetComponent<InteractionPanelPreferencesComponent>(target).Profile = new HumanoidCharacterProfile();
            system.AdjustArousal(source, target, state.Maximum);
            Assert.That(state.Current, Is.Zero);
            Assert.That(state.RecoveryUntil, Is.GreaterThan(pair.Server.ResolveDependency<IGameTiming>().CurTime));
            system.AdjustArousal(source, target, 0, interaction: true);
            system.AdjustArousal(source, target, 10);
            Assert.That(state.Current, Is.EqualTo(30), "Recovery permits gain, while neutral gain requires an explicit player setting");
            entities.DeleteEntity(source);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CustomChanceHonorsTheRequestedStrictThirtyPercentThreshold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        await pair.Server.WaitPost(() =>
        {
            var e = pair.Server.EntMan;
            user = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
        });
        await pair.Client.WaitPost(() =>
        {
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.AutomaticMoans, true);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.CustomMoanChance, true);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.MoanChance, 100f);
            pair.Client.CfgMan.SetCVar(InteractionPanelCVars.ArousalMultiplier, 100f);
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var listener = e.System<MoanListener>();
            listener.Counts.Clear();
            var state = e.GetComponent<InteractionPanelArousalComponent>(user);
            var system = e.System<InteractionPanelArousalSystem>();
            state.Current = 89;
            system.AdjustArousal(user, user, 1);
            Assert.That(listener.Counts.GetValueOrDefault(user), Is.Zero, "Exactly 30 percent remains silent");
            system.AdjustArousal(user, user, 1);
            Assert.That(listener.Counts.GetValueOrDefault(user), Is.EqualTo(1), "Custom 100 percent chance applies above 30 percent");
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MoansAreTriggeredByStimulationAndRespectSwitches()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            user = e.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            e.System<MoanListener>().Counts.Clear();
            e.GetComponent<InteractionPanelArousalComponent>(user).Current = 299;
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var arousal = e.GetComponent<InteractionPanelArousalComponent>(user);
            var counts = e.System<MoanListener>().Counts;
            Assert.That(counts.GetValueOrDefault(user), Is.Zero, "Time passing alone never triggers the reference moan handler");
            arousal.Current = 299;
            e.System<InteractionPanelArousalSystem>().AdjustArousal(user, user, 1);
            Assert.That(counts.GetValueOrDefault(user), Is.EqualTo(1), "A positive interaction at the threshold has 100 percent native chance");
            arousal.AutomaticMoans = false;
            arousal.Current = 299;
            e.System<InteractionPanelArousalSystem>().AdjustArousal(user, user, 1);
            Assert.That(counts.GetValueOrDefault(user), Is.EqualTo(1));
            arousal.AutomaticMoans = true;
            arousal.Current = 0;
            e.System<InteractionPanelArousalSystem>().AdjustArousal(user, user, 1);
            Assert.That(counts.GetValueOrDefault(user), Is.EqualTo(1), "The default curve is silent below half the threshold");
            e.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }
}
