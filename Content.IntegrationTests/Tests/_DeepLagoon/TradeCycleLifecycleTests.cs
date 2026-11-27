using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared.GameTicking;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TradeCycleLifecycleTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task)
    { tasks.BlockWaitOnTask(task); return task.GetAwaiter().GetResult(); }

    [Test]
    public async Task StartupCleanupKeepsListingsButTheRealEndEventCommitsTheirClosure()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        var market = server.EntMan.System<PlayerTradeSystem>();
        await server.WaitAssertion(() => server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true));
        await PoolManager.WaitUntil(server, () => market.TradeLifecycleReady, 3600);
        await server.WaitAssertion(() =>
        {
            var e = server.EntMan;
            var db = server.ResolveDependency<IServerDbManager>();
            var tasks = server.ResolveDependency<ITaskManager>();
            var session = server.PlayerMan.Sessions.Single();
            var slot = server.ResolveDependency<IServerPreferencesManager>().GetPreferences(session.UserId).SelectedCharacterIndex;
            var account = Complete(tasks, db.ReadEconomyAccountAsync(session.UserId.UserId, slot));
            var cycle = market.TradeCycleIdentity;
            var item = new TradeItem("SheetSteel", "Steel", "PERSISTENT SNAPSHOT", 100, 0, 1, new());
            var create = new TradeCreate(Guid.NewGuid(), session.UserId.UserId, account.Profile, false,
                item.Prototype, item.Name, "Survives startup cleanup", 1, 10, new(), item, null) { Cycle = cycle };
            Complete(tasks, db.CreateTradeOfferAsync(create, SectorEconomySystem.UtcNow));
            e.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            Assert.That(Complete(tasks, db.ReadTradeBoardAsync(session.UserId.UserId, account.Profile)).Offers, Has.Length.EqualTo(1));
            Assert.That(market.TradeCycleIdentity, Is.EqualTo(cycle));
            e.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(213, TimeSpan.FromDays(7)));
            Assert.That(market.TradeLifecycleReady, Is.False);
            Assert.That(Complete(tasks, db.ReadTradeBoardAsync(session.UserId.UserId, account.Profile)).Offers, Is.Empty);
            var state = Complete(tasks, db.BeginTradeCycleAsync(false));
            Assert.That(state.Id, Is.EqualTo(cycle));
            Assert.That(state.Closed, Is.True, "The marker must be durable before the round-end event returns.");
            e.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(213, TimeSpan.FromDays(7)));
            Assert.That(Complete(tasks, db.BeginTradeCycleAsync(false)).Id, Is.EqualTo(cycle));
        });
        await pair.CleanReturnAsync();
    }
}
