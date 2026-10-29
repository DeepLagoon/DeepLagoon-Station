using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class InteractionPanelArousalTests
{
    [Test]
    public async Task OnlyNoBlocksOwnPanelWhileAskExecutesAndRepeats()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        EntityUid user = default, target = default;
        string actionId = "";
        float gain = 0;
        float beforeNo = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var coords = new EntityCoordinates(map.Grid, new Vector2(0.5f, 0.5f));
            user = e.SpawnEntity("MobHuman", coords);
            target = e.SpawnEntity("MobHuman", coords);
            foreach (var session in sessions)
                pair.Server.PlayerMan.SetStatus(session, Robust.Shared.Enums.SessionStatus.InGame);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], user);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], target);
            var panels = e.System<InteractionPanelSystem>();
            var allowed = typeof(InteractionPanelSystem).GetMethod("Allowed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var userPrefs = e.GetComponent<InteractionPanelPreferencesComponent>(user);
            var targetPrefs = e.GetComponent<InteractionPanelPreferencesComponent>(target);
            foreach (var category in Enum.GetValues<InteractionPanelCategory>())
            foreach (var userConsent in Enum.GetValues<InteractionPanelConsent>())
            foreach (var targetConsent in Enum.GetValues<InteractionPanelConsent>())
            {
                userPrefs.Profile = userPrefs.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.Yes)
                    .WithInteractionPanelConsent(category, userConsent);
                targetPrefs.Profile = targetPrefs.Profile.WithInteractionPanelConsent(category, targetConsent);
                var custom = new InteractionPanelCustomAction { Category = category };
                Assert.That(allowed.Invoke(panels, [user, target, custom.Definition()]), Is.EqualTo(category == InteractionPanelCategory.Neutral || userConsent != InteractionPanelConsent.No),
                    $"{category}: {userConsent} -> {targetConsent}");
            }
            userPrefs.Profile = userPrefs.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.No)
                .WithInteractionPanelConsent(InteractionPanelCategory.NonCon, InteractionPanelConsent.Yes)
                .WithInteractionPanelConsent(InteractionPanelCategory.Vore, InteractionPanelConsent.Yes);
            foreach (var category in Enum.GetValues<InteractionPanelCategory>())
                Assert.That(allowed.Invoke(panels, [user, target, new InteractionPanelCustomAction { Category = category }.Definition()]),
                    Is.EqualTo(category == InteractionPanelCategory.Neutral), "ERP No hides every non-neutral category");

            var action = InteractionPanelActionCatalog.Load(pair.Server.ResolveDependency<IPrototypeManager>())
                .First(a => a.Category != InteractionPanelCategory.Neutral && (a.UserArousalGain ?? a.ArousalGain) is > 0 and <= 20 &&
                    (a.TargetArousalGain ?? a.ArousalGain) == (a.UserArousalGain ?? a.ArousalGain) &&
                    a.Supports(false, true) && a.UserSexes == null && a.TargetSexes == null);
            actionId = action.Id;
            gain = action.UserArousalGain ?? action.ArousalGain;
            userPrefs.Profile = userPrefs.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.Ask)
                .WithInteractionPanelConsent(action.Category, InteractionPanelConsent.Ask);
            targetPrefs.Profile = targetPrefs.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.No)
                .WithInteractionPanelConsent(action.Category, InteractionPanelConsent.No);
            panels.OpenPanel(user, target);
            Assert.That(panels.CanAccessEquipment(user, target), Is.True);
            var click = typeof(InteractionPanelSystem).GetMethod("OnAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var args = new InteractionPanelActionEvent(e.GetNetEntity(target), actionId);
            click.Invoke(panels, [args, new EntitySessionEventArgs(sessions[0])]);
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.EqualTo(gain));
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(target).Current, Is.EqualTo(gain),
                "Ask executes immediately; the target's No does not restrict the initiator's panel");
            click.Invoke(panels, [args, new EntitySessionEventArgs(sessions[0])]);
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.EqualTo(gain), "Cooldown still applies");
            typeof(InteractionPanelSystem).GetMethod("OnRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panels, [new InteractionPanelRepeatEvent(e.GetNetEntity(target), actionId, true, 1), new EntitySessionEventArgs(sessions[0])]);
        });
        await pair.RunTicksSync(150);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.GreaterThan(gain));
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(target).Current, Is.GreaterThan(gain));
            beforeNo = e.GetComponent<InteractionPanelArousalComponent>(user).Current;
            var preferences = e.GetComponent<InteractionPanelPreferencesComponent>(user);
            var profile = new InteractionPanelProfileLoadedEvent(preferences.Profile.WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.No));
            e.EventBus.RaiseLocalEvent(user, ref profile);
            typeof(InteractionPanelSystem).GetMethod("OnAction", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(e.System<InteractionPanelSystem>(), [new InteractionPanelActionEvent(e.GetNetEntity(target), actionId), new EntitySessionEventArgs(sessions[0])]);
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.EqualTo(beforeNo), "No rejects a direct network action");
        });
        await pair.RunTicksSync(150);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.GetComponent<InteractionPanelArousalComponent>(user).Current, Is.LessThanOrEqualTo(beforeNo), "No prevents further gains while arousal can decay");
            var panels = e.System<InteractionPanelSystem>();
            var repeats = (IDictionary) typeof(InteractionPanelSystem).GetField("_repeats", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panels)!;
            Assert.That(repeats.Count, Is.Zero, "No stops blocked repeats");
            typeof(InteractionPanelSystem).GetMethod("OnRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panels, [new InteractionPanelRepeatEvent(e.GetNetEntity(target), "", false, 1), new EntitySessionEventArgs(sessions[0])]);
            Assert.That(repeats.Count, Is.Zero, "Stop remains available");
            pair.Server.PlayerMan.SetAttachedEntity(sessions[0], null);
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], null);
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.Server.RemoveAllDummySessions();
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExecutionAwardsBothOnceAndAllowsGainDuringRecovery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var user = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var target = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var panels = entities.System<InteractionPanelSystem>();
            var userArousal = entities.GetComponent<InteractionPanelArousalComponent>(user);
            var targetArousal = entities.GetComponent<InteractionPanelArousalComponent>(target);
            var action = new InteractionPanelActionDefinition("test", "all", InteractionPanelCategory.Neutral,
                Template: "{user} greets {target}.", ArousalGain: 8);
            void Execute(EntityUid other) =>
                typeof(InteractionPanelSystem).GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(panels, [user, other, action, null]);

            Execute(target);
            Assert.Multiple(() =>
            {
                Assert.That(userArousal.Current, Is.EqualTo(8), "The initiator receives the full amount");
                Assert.That(targetArousal.Current, Is.EqualTo(8), "The target receives the full amount");
            });

            Execute(user);
            Assert.That(userArousal.Current, Is.EqualTo(16), "Self interaction awards once");
            Assert.That(targetArousal.Current, Is.EqualTo(8));

            var recovery = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(30);
            targetArousal.RecoveryUntil = recovery;
            Execute(target);
            Assert.That(userArousal.Current, Is.EqualTo(24), "Target recovery does not block the initiator");
            Assert.That(targetArousal.Current, Is.EqualTo(16));

            targetArousal.RecoveryUntil = TimeSpan.Zero;
            userArousal.RecoveryUntil = recovery;
            Execute(target);
            Assert.That(userArousal.Current, Is.EqualTo(32));
            Assert.That(targetArousal.Current, Is.EqualTo(24), "Initiator recovery does not block the target");

            entities.DeleteEntity(user);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }
}
