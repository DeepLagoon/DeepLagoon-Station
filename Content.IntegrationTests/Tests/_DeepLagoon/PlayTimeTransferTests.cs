using System.Collections.Generic;
using System.Linq;
using Content.Server.Database;
using Content.Server.Players.PlayTimeTracking;
using Content.Server._Goobstation.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._Goobstation.Administration;
using Content.Shared.Players.PlayTimeTracking;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class PlayTimeTransferTests
{
    [Test]
    public async Task OnlineAwardUpdatesMemoryClientAndDatabaseAndSurvivesSave()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var manager = pair.Server.ResolveDependency<PlayTimeTrackingManager>();
        var database = pair.Server.ResolveDependency<IServerDbManager>();
        var user = default(NetUserId);
        TimeTransferPanelEui panel = null!;
        Task operation = Task.CompletedTask;
        await pair.Server.WaitPost(() =>
        {
            user = pair.Server.PlayerMan.Sessions.Single().UserId;
            operation = manager.UpdateTrackerTimesAsync(user, new Dictionary<string, TimeSpan>
                { ["JobCaptain"] = TimeSpan.FromMinutes(10) }, true);
        });
        await PoolManager.WaitUntil(pair.Server, () => operation.IsCompleted);
        await operation;
        await pair.Server.WaitPost(() =>
        {
            var session = pair.Server.PlayerMan.Sessions.Single();
            pair.Server.ResolveDependency<IAdminManager>().PromoteHost(session);
            panel = new TimeTransferPanelEui();
            pair.Server.ResolveDependency<EuiManager>().OpenEui(panel, session);
            panel.HandleMessage(new TimeTransferEuiMessage(" " + session.Name + " ",
                [new TimeTransferData("JobCaptain", "5м")], false));
        });
        await PoolManager.WaitUntil(pair.Server, async () =>
            (await database.GetPlayTimes(user)).Single(t => t.Tracker == "JobCaptain").TimeSpent == TimeSpan.FromMinutes(15));
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(manager.GetPlayTimeForTracker(pair.Server.PlayerMan.Sessions.Single(), "JobCaptain"),
                Is.EqualTo(TimeSpan.FromMinutes(15)));
            manager.Save();
            operation = manager.WaitPendingSavesAsync();
        });
        await PoolManager.WaitUntil(pair.Server, () => operation.IsCompleted);
        await operation;
        Assert.That((await database.GetPlayTimes(user)).Single(t => t.Tracker == "JobCaptain").TimeSpent,
            Is.EqualTo(TimeSpan.FromMinutes(15)), "Autosave must not restore the old database value.");
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var session = pair.Client.ResolveDependency<Robust.Client.Player.IPlayerManager>().LocalSession;
            var clientTimes = pair.Client.ResolveDependency<ISharedPlaytimeManager>().GetPlayTimes(session);
            Assert.That(clientTimes["JobCaptain"], Is.EqualTo(TimeSpan.FromMinutes(15)));
        });
        await pair.Server.WaitPost(() => panel.Close());
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OfflineConcurrentAwardsAccumulateAndOverwritePersists()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var manager = pair.Server.ResolveDependency<PlayTimeTrackingManager>();
        var database = pair.Server.ResolveDependency<IServerDbManager>();
        var user = new NetUserId(Guid.NewGuid());
        Task operation = Task.CompletedTask;
        await pair.Server.WaitPost(() => operation = Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            manager.UpdateTrackerTimesAsync(user,
                new Dictionary<string, TimeSpan> { ["JobCaptain"] = TimeSpan.FromMinutes(5) }, false))));
        await PoolManager.WaitUntil(pair.Server, () => operation.IsCompleted);
        await operation;
        Assert.That((await database.GetPlayTimes(user)).Single().TimeSpent, Is.EqualTo(TimeSpan.FromMinutes(15)));
        await pair.Server.WaitPost(() => operation = manager.UpdateTrackerTimesAsync(user,
            new Dictionary<string, TimeSpan> { ["JobCaptain"] = TimeSpan.Zero }, true));
        await PoolManager.WaitUntil(pair.Server, () => operation.IsCompleted);
        await operation;
        Assert.That((await database.GetPlayTimes(user)).Single().TimeSpent, Is.EqualTo(TimeSpan.Zero));
        await pair.CleanReturnAsync();
    }
}
