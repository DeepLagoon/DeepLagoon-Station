using System.Linq;
using System.Collections;
using System.Reflection;
using Content.Client._DeepLagoon.Currency;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;
using System.Collections.Generic;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Objectives.Components;
using Content.Shared.GameTicking;
using Content.Shared._DeepLagoon.Currency;
using Content.Shared.Mind;
using Content.Shared.Objectives.Components;
using Content.Shared.Preferences;
using Content.Shared.Database;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;
using ServerWallet = Content.Server._DeepLagoon.Currency.LagoonCoinSystem;
using ClientWallet = Content.Client._DeepLagoon.Currency.LagoonCoinSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LagoonCoinIntegrationTests
{
    [Test]
    public async Task WalletOperationsPublishOnceToOnlineOwnerAndNeverChangeCharacterBank()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        List<LagoonCoinRewardEvent> rewards = null!;
        var initialBankBalance = 0;
        await pair.Client.WaitPost(() =>
        {
            var recorder = pair.Client.EntMan.System<RewardRecorder>();
            recorder.Enabled = true;
            rewards = recorder.Rewards;
            rewards.Clear();
        });
        EntityUid mob = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var player = pair.Server.PlayerMan.Sessions.Single();
            var ticker = entities.System<GameTicker>();
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            initialBankBalance = ReadBankBalance(pair.Server.ResolveDependency<IServerDbManager>(), tasks, player.UserId);

            ticker.ToggleReady(player, true);
            ticker.ToggleReady(player, false);
            ticker.ToggleReady(player, true);
            mob = entities.SpawnEntity("MobHumanDummy", map.GridCoords);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var spawned = new PlayerSpawnCompleteEvent(mob, player, null, false, false, 1, map.MapUid, profile);
            entities.EventBus.RaiseEvent(EventSource.Local, spawned);
            entities.EventBus.RaiseEvent(EventSource.Local, spawned);
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundStartedEvent(123));
            var wallet = entities.System<ServerWallet>();
            wallet.Update(0);
            SeedActivity(wallet, player, LagoonEconomyRules.ReadyActivity.Ticks, 2, "123", true);
            typeof(ServerWallet).GetMethod("SaveParticipation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wallet, null);
            wallet.Update(0);
            AwaitRewards(pair.Server.ResolveDependency<ITaskManager>(), wallet);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.ResolveDependency<IEntityManager>().System<ClientWallet>().Balance, Is.EqualTo(5));
            Assert.That(rewards.Select(ev => (ev.Amount, ev.Balance, ev.Reason)),
                Is.EqualTo(new[] { (5L, 5L, "playtime") }));
        });

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var player = pair.Server.PlayerMan.Sessions.Single();
            var mind = entities.System<MindSystem>().GetOrCreateMind(player.UserId);
            entities.System<MindSystem>().TransferTo(mind.Owner, mob, mind: mind.Comp);
            entities.System<SharedRoleSystem>().MindAddRole(mind.Owner, "MindRoleTraitor", mind.Comp, silent: true);
            var objective = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<ObjectiveComponent>(objective);
            entities.EnsureComponent<FreeObjectiveComponent>(objective);
            mind.Comp.Objectives.Add(objective);
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(123, TimeSpan.FromMinutes(30)));
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(123, TimeSpan.FromMinutes(30)));
            entities.System<ServerWallet>().Update(0);
            AwaitRewards(pair.Server.ResolveDependency<ITaskManager>(), entities.System<ServerWallet>());
            var database = pair.Server.ResolveDependency<IServerDbManager>();
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            var history = database.GetLagoonCoinHistoryAsync(player.UserId, 1);
            tasks.BlockWaitOnTask(history);
            Assert.That(history.GetAwaiter().GetResult().Count(entry => entry.Amount == 5 && entry.Reason == "ready"), Is.EqualTo(1));
            Assert.That(ReadBankBalance(database, tasks, player.UserId), Is.EqualTo(initialBankBalance));
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.ResolveDependency<IEntityManager>().System<ClientWallet>().Balance, Is.EqualTo(7));
            Assert.That(rewards.Select(ev => (ev.Amount, ev.Balance, ev.Reason)),
                Is.EqualTo(new[] { (5L, 5L, "playtime"), (2L, 7L, "antagonist-objective") }));
            pair.Client.ResolveDependency<IUserInterfaceManager>().PopupRoot.Children.OfType<LagoonCoinRewardNotification>().Single().Clear();
        });
        await pair.Server.WaitAssertion(() =>
        {
            var player = pair.Server.PlayerMan.Sessions.Single();
            var wallet = pair.Server.EntMan.System<ServerWallet>();
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            var debit = wallet.Deduct(player.UserId, 3, "Discord test debit", null, "discord:integration-debit");
            tasks.BlockWaitOnTask(debit);
            Assert.That(debit.GetAwaiter().GetResult().Balance, Is.EqualTo(4));
            var retry = wallet.Deduct(player.UserId, 3, "Discord test debit", null, "discord:integration-debit");
            tasks.BlockWaitOnTask(retry);
            Assert.That(retry.GetAwaiter().GetResult().Applied, Is.False);
            var database = pair.Server.ResolveDependency<IServerDbManager>();
            var history = database.GetLagoonCoinHistoryAsync(player.UserId, 1);
            tasks.BlockWaitOnTask(history);
            Assert.That(history.GetAwaiter().GetResult().Any(r => r.Amount == -3), Is.True);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(pair.Client.ResolveDependency<IEntityManager>().System<ClientWallet>().Balance, Is.EqualTo(4));
            Assert.That(rewards.Select(ev => (ev.Amount, ev.Balance, ev.Reason)),
                Is.EqualTo(new[] { (5L, 5L, "playtime"), (2L, 7L, "antagonist-objective"), (-3L, 4L, "Discord test debit") }));
            AssertNotification(-3, 4, "Discord test debit");
        });

        async Task CheckOperation(Func<ServerWallet, NetUserId, Task<LagoonCoinResult>> operation,
            long amount, long balance, string reason, int count)
        {
            await pair.Server.WaitAssertion(() =>
            {
                var player = pair.Server.PlayerMan.Sessions.Single();
                var wallet = pair.Server.EntMan.System<ServerWallet>();
                var tasks = pair.Server.ResolveDependency<ITaskManager>();
                var applied = operation(wallet, player.UserId);
                tasks.BlockWaitOnTask(applied);
                Assert.That(applied.GetAwaiter().GetResult(), Is.EqualTo(new LagoonCoinResult(balance, amount, true)));
                var retry = operation(wallet, player.UserId);
                tasks.BlockWaitOnTask(retry);
                Assert.That(retry.GetAwaiter().GetResult().Applied, Is.False);
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                Assert.That(pair.Client.EntMan.System<ClientWallet>().Balance, Is.EqualTo(balance));
                Assert.That(rewards.Count, Is.EqualTo(count), "A retry must not announce the operation again.");
                Assert.That((rewards.Last().Amount, rewards.Last().Balance, rewards.Last().Reason), Is.EqualTo((amount, balance, reason)));
                AssertNotification(amount, balance, reason);
            });
        }

        void AssertNotification(long amount, long balance, string reason)
        {
            pair.Client.EntMan.System<ClientWallet>().FrameUpdate(0);
            var notification = pair.Client.ResolveDependency<IUserInterfaceManager>().PopupRoot.Children.OfType<LagoonCoinRewardNotification>().Single();
            Assert.That(Find<PanelContainer>(notification, "RewardPanel").Visible, Is.True);
            Assert.That(Find<Label>(notification, "RewardTitle").Text,
                Is.EqualTo(Robust.Shared.Localization.Loc.GetString(amount > 0 ? "lagoon-coin-reward-title" : "lagoon-coin-debit-title", ("amount", amount))));
            Assert.That(Find<Label>(notification, "RewardDetail").Text,
                Is.EqualTo(Robust.Shared.Localization.Loc.GetString("lagoon-coin-reward-detail", ("reason", reason), ("balance", balance))));
            notification.Clear();
        }

        await CheckOperation((wallet, user) => wallet.Grant(user, 10, "Administrator grant", null, "admin:integration-grant"),
            10, 14, "Administrator grant", 4);
        await CheckOperation((wallet, user) => wallet.RecordPurchase(user, 20, "integration-purchase", "Verified payment"),
            20, 34, "Verified payment", 5);
        await CheckOperation((wallet, user) => wallet.Spend(user, 7, "integration-spend", "Shop purchase"),
            -7, 27, "Shop purchase", 6);

        await pair.Server.WaitAssertion(() =>
        {
            var user = pair.Server.PlayerMan.Sessions.Single().UserId;
            var wallet = pair.Server.EntMan.System<ServerWallet>();
            var database = pair.Server.ResolveDependency<IServerDbManager>();
            var tasks = pair.Server.ResolveDependency<ITaskManager>();
            var failed = wallet.Spend(user, 100, "integration-insufficient", "Too expensive");
            tasks.BlockWaitOnTask(failed);
            Assert.Throws<InsufficientLagoonCoinsException>(() => failed.GetAwaiter().GetResult());

            var offline = new NetUserId(Guid.NewGuid());
            var preferences = database.InitPrefsAsync(offline, HumanoidCharacterProfile.DefaultWithSpecies("Human"), default);
            tasks.BlockWaitOnTask(preferences);
            preferences.GetAwaiter().GetResult();
            var grant = wallet.Grant(offline, 9, "Offline grant", null, "admin:integration-offline");
            tasks.BlockWaitOnTask(grant);
            Assert.That(grant.GetAwaiter().GetResult().Balance, Is.EqualTo(9));
            Assert.That(ReadBankBalance(database, tasks, user), Is.EqualTo(initialBankBalance));
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var wallet = pair.Client.EntMan.System<ClientWallet>();
            wallet.FrameUpdate(0);
            Assert.That(wallet.Balance, Is.EqualTo(27));
            Assert.That(rewards.Count, Is.EqualTo(6), "Failed operations and another user's offline grant must not be announced.");
            var notification = pair.Client.ResolveDependency<IUserInterfaceManager>().PopupRoot.Children.OfType<LagoonCoinRewardNotification>().Single();
            Assert.That(Find<PanelContainer>(notification, "RewardPanel").Visible, Is.False);
            pair.Client.EntMan.System<RewardRecorder>().Enabled = false;
            rewards.Clear();
        });
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(mob));
        await pair.CleanReturnAsync();
    }

    [TestCase(false, 5)]
    [TestCase(true, 7)]
    public async Task PlaytimeRewardPublishesOnceAboveTheGameViewport(bool subscribed, long expected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        List<LagoonCoinRewardEvent> rewards = null!;
        var initialBankBalance = 0;
        await pair.Client.WaitPost(() =>
        {
            var recorder = pair.Client.EntMan.System<RewardRecorder>();
            recorder.Enabled = true;
            rewards = recorder.Rewards;
            rewards.Clear();
        });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            initialBankBalance = ReadBankBalance(pair.Server.ResolveDependency<IServerDbManager>(),
                pair.Server.ResolveDependency<ITaskManager>(), session.UserId);
        });

        async Task SaveActivity(long ticks) => await pair.Server.WaitAssertion(() =>
        {
            var wallet = pair.Server.EntMan.System<ServerWallet>();
            wallet.Update(0);
            var session = pair.Server.PlayerMan.Sessions.Single();
            // Seed the same rate/day buckets consumed by the production outbox.
            SeedActivity(wallet, session, ticks, subscribed ? 4 : 2, "", false);
            typeof(ServerWallet).GetMethod("SaveParticipation", flags)!.Invoke(wallet, null);
            wallet.Update(0);
            var drain = (Task) typeof(ServerWallet).GetField("_drainTask", flags)!.GetValue(wallet)!;
            pair.Server.ResolveDependency<ITaskManager>().BlockWaitOnTask(drain);
            drain.GetAwaiter().GetResult();
        });

        await SaveActivity(TimeSpan.TicksPerHour / 8);
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.EntMan.System<ClientWallet>().FrameUpdate(0);
            var notification = pair.Client.ResolveDependency<IUserInterfaceManager>().PopupRoot.Children.OfType<LagoonCoinRewardNotification>().Single();
            Assert.That(Find<PanelContainer>(notification, "RewardPanel").Visible, Is.False);
            Assert.That(rewards, Is.Empty, "A fractional-hour checkpoint must not display a zero reward.");
        });

        await SaveActivity(TimeSpan.TicksPerHour * 7 / 8);
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var wallet = pair.Client.EntMan.System<ClientWallet>();
            wallet.FrameUpdate(0);
            Assert.That(wallet.Balance, Is.EqualTo(expected));
            Assert.That(rewards.Select(ev => (ev.Amount, ev.Balance, ev.Reason)), Is.EqualTo(new[] { (expected, expected, "playtime") }));
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var notification = ui.PopupRoot.Children.OfType<LagoonCoinRewardNotification>().Single();
            Assert.That(notification.Parent, Is.SameAs(ui.PopupRoot), "Reward text must be above gameplay and browser panels.");
            Assert.That(Find<PanelContainer>(notification, "RewardPanel").Visible, Is.True);
            Assert.That(Find<Label>(notification, "RewardTitle").Text,
                Is.EqualTo(Robust.Shared.Localization.Loc.GetString("lagoon-coin-reward-title", ("amount", expected))));
            var detail = Find<Label>(notification, "RewardDetail").Text;
            Assert.That(detail, Is.EqualTo(Robust.Shared.Localization.Loc.GetString("lagoon-coin-reward-detail",
                ("reason", Robust.Shared.Localization.Loc.GetString("lagoon-coin-reason-playtime")), ("balance", expected))));
            Assert.That(notification.MouseFilter, Is.EqualTo(Control.MouseFilterMode.Ignore));
            notification.Clear();
            wallet.FrameUpdate(0);
            Assert.That(Find<PanelContainer>(notification, "RewardPanel").Visible, Is.False);
            pair.Client.EntMan.System<RewardRecorder>().Enabled = false;
            rewards.Clear();
        });
        await pair.Server.WaitAssertion(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            var database = pair.Server.ResolveDependency<IServerDbManager>();
            Assert.That(ReadBankBalance(database, pair.Server.ResolveDependency<ITaskManager>(), session.UserId), Is.EqualTo(initialBankBalance));
        });
        await pair.CleanReturnAsync();
    }


    private static int ReadBankBalance(IServerDbManager database, ITaskManager tasks, NetUserId user)
    {
        var preferences = database.GetPlayerPreferencesAsync(user, default);
        tasks.BlockWaitOnTask(preferences);
        return ((HumanoidCharacterProfile) preferences.GetAwaiter().GetResult()!.SelectedCharacter).BankBalance;
    }

    private static void SeedActivity(ServerWallet wallet, Robust.Shared.Player.ICommonSession session,
        long ticks, int rate, string round, bool ready)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var participation = (IDictionary) typeof(ServerWallet).GetField("_participation", flags)!.GetValue(wallet)!;
        var data = participation[session]!;
        data.GetType().GetField("Active")!.SetValue(data, false);
        var buckets = (IDictionary) data.GetType().GetField("Buckets")!.GetValue(data)!;
        var bucketType = typeof(ServerWallet).GetNestedType("ActivityBucket", BindingFlags.NonPublic)!;
        var bucket = Activator.CreateInstance(bucketType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { LagoonEconomyRules.MoscowDay(DateTime.UtcNow), rate, round, ready }, null)!;
        buckets[bucket] = ticks;
    }

    private static void AwaitRewards(ITaskManager tasks, ServerWallet wallet)
    {
        var drain = (Task) typeof(ServerWallet).GetField("_drainTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wallet)!;
        tasks.BlockWaitOnTask(drain);
        drain.GetAwaiter().GetResult();
    }

    private static T Find<T>(Control root, string name) where T : Control
        => Descendants(root).OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class RewardRecorder : EntitySystem
    {
        public bool Enabled;
        public readonly List<LagoonCoinRewardEvent> Rewards = new();

        public override void Initialize()
        {
            base.Initialize();
            SubscribeNetworkEvent<LagoonCoinRewardEvent>(ev =>
            {
                if (Enabled) Rewards.Add(ev);
            });
        }
    }

}
