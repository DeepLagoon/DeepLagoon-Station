using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Voyages;
using Content.Server.Database;
using Content.Server._NF.Bank;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.SectorServices;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorAutomaticResearchTests
{
    [Test]
    public async Task EveryReachableUnfinishedSectorGetsAGameOrderEvenWithoutAnyFactionBank()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var e = server.EntMan;
        var system = e.System<SectorExpeditionSystem>(); var voyages = e.System<VoyageSystem>();
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var heartbeat = typeof(SectorExpeditionSystem).GetField("_next", flags)!;
        SectorResearchBoard board = null!;
        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(SectorEconomyCVars.Enabled, true);
            server.CfgMan.SetCVar(SectorExpeditionCVars.Enabled, true);
            Assert.That(SectorExpeditionSystem.DebugResearchFunding, Is.True);
        });
        for (var i = 0; i < 1000; i++)
        {
            var ready = false;
            await server.WaitAssertion(() =>
            {
                heartbeat.SetValue(system, TimeSpan.Zero);
                if (!voyages.Ready) return;
                var read = db.ReadSectorResearchAsync(voyages.WorldSnapshot.Cycle, Guid.Empty, 0);
                tasks.BlockWaitOnTask(read); board = read.GetAwaiter().GetResult();
                ready = board.Orders.Any(o => o.Automatic) && !(bool)typeof(SectorExpeditionSystem).GetField("_autoOrdersBusy", flags)!.GetValue(system)!;
            });
            if (ready) break;
            await server.WaitRunTicks(5); await Task.Delay(10);
        }
        await server.WaitAssertion(() =>
        {
            Assert.That(e.EntityQuery<SectorBankComponent>(), Is.Empty, "This fixture has no faction account to fund orders.");
            Assert.That(board, Is.Not.Null);
            var graph = voyages.RouteGraph;
            var expected = graph.Systems.Where(s => !SectorResearchDefinition.Settled(s.Id) &&
                (s.Known ? graph.Path(VoyageRouteModel.Home, s.Id) : graph.SurveyPath(s.Id)).Length > 1).Select(s => s.Id).ToArray();
            Assert.That(expected.Length, Is.GreaterThan(2));
            Assert.That(board.Orders.Where(o => o.Automatic).Select(o => o.Sector), Is.EquivalentTo(expected));
            Assert.That(board.Orders.All(o => o.Remaining > 0 && o.User == Guid.Empty), Is.True);
            var next = (TimeSpan)typeof(SectorExpeditionSystem).GetField("_nextAutoOrders", flags)!.GetValue(system)!;
            var now = server.ResolveDependency<IGameTiming>().RealTime;
            Assert.That((next - now).TotalSeconds, Is.InRange(895, 900));
            server.CfgMan.SetCVar(SectorResearchSettings.Budget, 42000);
            Assert.That((TimeSpan)typeof(SectorExpeditionSystem).GetField("_nextAutoOrders", flags)!.GetValue(system)!, Is.EqualTo(TimeSpan.Zero));
            server.CfgMan.SetCVar(SectorResearchSettings.RefreshSeconds, 45);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task FactionPublisherRequiresRealBankMoneyAndDoesNotReserveTheSameSectorTwice()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var e = server.EntMan; var system = e.System<SectorExpeditionSystem>();
        var db = server.ResolveDependency<IServerDbManager>(); var tasks = server.ResolveDependency<ITaskManager>();
        var voyages = e.System<VoyageSystem>();
        var read = db.ReadVoyageWorldAsync(true, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var world = await read;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        async Task Publish()
        {
            Task pending = null!;
            await server.WaitAssertion(() => pending = (Task)typeof(SectorExpeditionSystem).GetMethod("PublishFactionResearch", flags)!.Invoke(system, [world, new AutomaticSectorResearchTarget[] { new("echo", 3, 6000) }])!);
            for (var i = 0; i < 100 && !pending.IsCompleted; i++) { await server.WaitRunTicks(1); await Task.Delay(10); }
            await pending;
        }
        await Publish();
        Assert.That((await db.ReadSectorResearchAsync(world.Cycle, Guid.Empty, 0)).Orders, Is.Empty);
        EntityUid bankUid = default;
        await server.WaitAssertion(() =>
        {
            var host = e.SpawnEntity(null, Robust.Shared.Map.MapCoordinates.Nullspace);
            e.AddComponent<StationSectorServiceHostComponent>(host);
            bankUid = e.System<SectorServiceSystem>().GetServiceEntity();
            var bank = e.GetComponent<SectorBankComponent>(bankUid);
            foreach (var (kind, account) in bank.Accounts) account.Balance = kind == SectorBankAccount.Frontier ? 100000 : 0;
        });
        await Publish(); await Publish();
        var board = await db.ReadSectorResearchAsync(world.Cycle, Guid.Empty, 0);
        Assert.That(board.Orders, Has.Length.EqualTo(1)); Assert.That(board.Orders[0].DebugFunded, Is.False);
        Assert.That(board.Orders[0].Faction, Is.EqualTo((byte)SectorBankAccount.Frontier));
        await server.WaitAssertion(() => Assert.That(e.GetComponent<SectorBankComponent>(bankUid).Accounts[SectorBankAccount.Frontier].Balance, Is.EqualTo(94000)));
        await pair.CleanReturnAsync();
    }

}
