using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Voyages;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SectorResearchLifecycleTests
{
    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    [Test]
    public async Task ForcedEndAndImmediateRestartPreserveResearchButNormalEndStartsANewCycle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server; var entities = server.EntMan;
        var manager = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        var voyages = entities.System<VoyageSystem>();
        void Complete(Task task) { tasks.BlockWaitOnTask(task); task.GetAwaiter().GetResult(); }
        await server.WaitAssertion(() =>
        {
            Complete(voyages.RefreshGalaxy());
            var cycle = voyages.WorldSnapshot.Cycle;
            var backend = Field<ServerDbBase>(manager, "_db");
            var options = Field<Func<DbContextOptions<SqliteServerDbContext>>>(backend, "_options")();
            using (var db = new SqliteServerDbContext(options))
            {
                var row = db.LagoonEconomyRecords.Single(r => r.Id == "voyage:world:v2");
                var world = JsonSerializer.Deserialize<VoyageWorld>(row.Data)!;
                world.Research["echo"] = new SectorResearchProgress { Completed = [0, 2] };
                row.Data = JsonSerializer.Serialize(world); db.SaveChanges();
            }
            Complete(voyages.RefreshGalaxy());
            void AssertPreserved()
            {
                Complete(voyages.RefreshGalaxy());
                Assert.That(voyages.WorldSnapshot.Cycle, Is.EqualTo(cycle));
                Assert.That(voyages.WorldSnapshot.Closed, Is.False);
                Assert.That(voyages.WorldSnapshot.Research["echo"].Completed, Is.EquivalentTo(new[] { 0, 2 }));
            }
            // Cover both lifecycle notifications emitted by endround/restartround/API.
            var ticker = entities.System<GameTicker>();
            var forced = typeof(GameTicker).GetProperty(nameof(GameTicker.RoundEndForced))!;
            forced.SetValue(ticker, true);
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound));
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(1, TimeSpan.FromMinutes(5), forced: true));
            AssertPreserved();
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound));
            AssertPreserved();
            // restartroundnow can skip PostRound entirely.
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
            AssertPreserved();
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
            forced.SetValue(ticker, false);
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound));
            entities.EventBus.RaiseEvent(EventSource.Local, new RoundEndedEvent(2, TimeSpan.FromDays(7)));
            Complete(voyages.RefreshGalaxy());
            Assert.That(voyages.WorldSnapshot.Closed, Is.True);
            entities.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound));
            Complete(voyages.RefreshGalaxy());
            Assert.That(voyages.WorldSnapshot.Cycle, Is.Not.EqualTo(cycle));
            Assert.That(voyages.WorldSnapshot.Research, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }
}
