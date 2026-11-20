using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Paper;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Robust.Shared.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.Markdown.Sequence;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentWriterPreparationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task FrozenDuringPendingWriterPreparationSkipsCheckpointButStillSavesBeforeUnload()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var em = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var system = em.System<ApartmentSystem>();
            var body = em.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(system.Enter(session, session.UserId, out var message), Is.True, message);
            var home = system.Instances[session.UserId];
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(system)!;
            var previous = store.LoadSnapshot(home.Owner.UserId, home.Template.ID)?.Grid;
            Assert.That(home.Frozen, Is.False);
            var checkpoint = system.SaveApartmentAsync(home, ApartmentSystem.ApartmentSaveReason.Checkpoint);
            Assert.That(checkpoint.IsCompleted, Is.False, "This regression exercises the pending cold-preparation boundary, not a completed save.");
            Assert.That(system.Leave(session.UserId, out message), Is.True, message);
            Assert.That(home.Frozen, Is.True);
            var tasks = em.EntitySysManager.DependencyCollection.Resolve<ITaskManager>();
            tasks.BlockWaitOnTask(checkpoint);
            Assert.That(checkpoint.GetAwaiter().GetResult(), Is.False);
            Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID)?.Grid, Is.EqualTo(previous),
                "A checkpoint that becomes frozen during preparation must not replace the durable snapshot.");

            var unload = system.SaveApartmentAsync(home, ApartmentSystem.ApartmentSaveReason.Unload);
            tasks.BlockWaitOnTask(unload);
            Assert.That(unload.GetAwaiter().GetResult(), Is.True, "Frozen apartments still require a durable save before unloading.");
            Assert.That(store.LoadSnapshot(home.Owner.UserId, home.Template.ID), Is.Not.Null);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeApartmentPreparesOnWorkerAndDurablySavesLateAddedPaper(bool latePaper)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        ApartmentSystem system = null!;
        ApartmentSystem.Instance home = null!;
        Task<WriterPreparationResult> preparation = null!;
        Task<bool> saved = null!;
        var gameThread = 0;
        var expectedEntities = 59;
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.CfgMan.SetCVar(ApartmentCVars.Enabled, true);
            pair.Server.CfgMan.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            pair.Server.CfgMan.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, 2);
            var session = pair.Server.PlayerMan.Sessions.Single();
            var em = pair.Server.EntMan;
            system = em.System<ApartmentSystem>();
            var body = em.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(system.Enter(session, session.UserId, out var message), Is.True, message);
            home = system.Instances[session.UserId];
            gameThread = Environment.CurrentManagedThreadId;
            preparation = (Task<WriterPreparationResult>) typeof(ApartmentSystem)
                .GetMethod("PrepareApartmentWritersAsync", Private)!.Invoke(system, new object[] { home })!;
            saved = system.SaveApartmentAsync(home);
            // Materialize a new component/prototype AFTER metadata collection. A later save
            // must include it; no graph, writer set, or revision is frozen before the await.
            if (latePaper)
            {
                var paper = em.SpawnEntity("Paper", new Robust.Shared.Map.EntityCoordinates(home.Grid, 2.5f, 7.5f));
                em.GetComponent<PaperComponent>(paper).Content = "Late paper must survive writer preparation.";
                var children = em.GetComponent<TransformComponent>(paper).ChildEnumerator;
                var count = 0;
                while (children.MoveNext(out var child))
                {
                    count++;
                    Assert.That(em.HasComponent<SolutionComponent>(child), Is.True);
                    var relation = em.GetComponent<ContainedSolutionComponent>(child);
                    Assert.That(relation.Container, Is.EqualTo(paper));
                    Assert.That(relation.ContainerName, Is.EqualTo("food"));
                }
                Assert.That(count, Is.EqualTo(1), "Native Paper has one Fiber food solution child.");
                expectedEntities += 1 + count;
            }
        });
        // Native integration instances advance only on explicit messages/ticks. Pump the
        // normal main-thread task queue while the pure compiler and durable writer finish;
        // awaiting from the NUnit thread alone leaves their posted continuations idle.
        await pair.Server.WaitAssertion(() =>
            pair.Server.EntMan.EntitySysManager.DependencyCollection.Resolve<ITaskManager>()
                .BlockWaitOnTask(Task.WhenAll(new Task[] { preparation, saved })));
        var result = await preparation;
        Assert.That(result.ExpressionsCompiled, Is.GreaterThan(0));
        Assert.That(result.WorkerThreadId, Is.Not.EqualTo(gameThread));
        Assert.That(result.WorkerHadSynchronizationContext, Is.False);
        TestContext.WriteLine($"native_preparation plan_ms={result.PlanningMilliseconds:F3} queue_ms={result.QueueMilliseconds:F3} compile_ms={result.CompileMilliseconds:F3} types={result.PreparedTypes} expressions={result.ExpressionsCompiled}");

        Assert.That(await saved, Is.True);
        await pair.Server.WaitAssertion(() =>
        {
            var store = (IApartmentLayoutStore) typeof(ApartmentSystem).GetProperty("Store", Private)!.GetValue(system)!;
            var snapshot = store.LoadSnapshot(home.Owner.UserId, home.Template.ID)!;
            if (latePaper) Assert.That(snapshot.Grid, Does.Contain("Late paper must survive writer preparation."));
            using var reader = new StringReader(snapshot.Grid);
            var data = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
            var groups = data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().ToArray();
            var entries = groups.SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()).ToArray();
            Assert.That(entries.Length, Is.EqualTo(expectedEntities), "59 native apartment entities plus the complete late Paper subtree.");
            Assert.That(data.Get<MappingDataNode>("meta").Get<ValueDataNode>("entityCount").Value, Is.EqualTo(entries.Length.ToString()));
            var papers = groups.Where(group => group.Get<ValueDataNode>("proto").Value == "Paper")
                .SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()).ToArray();
            Assert.That(papers.Length, Is.EqualTo(latePaper ? 1 : 0));
            Assert.That(groups.Any(group => group.Get<ValueDataNode>("proto").Value == "MobHuman"), Is.False, "Visitor and child gear must remain excluded.");
            if (latePaper)
            {
                var paperId = papers.Single().Get<ValueDataNode>("uid").Value;
                var solutions = entries.Where(entry => entry.TryGet<SequenceDataNode>("components", out var components) &&
                    components.Cast<MappingDataNode>().Any(component => component.Get<ValueDataNode>("type").Value == "ContainedSolution" &&
                        component.Get<ValueDataNode>("container").Value == paperId && component.Get<ValueDataNode>("containerName").Value == "food"));
                Assert.That(solutions.Count(), Is.EqualTo(1), "The extra entity is the Paper's native food solution, not an orphan or visitor.");
            }
        });
        await pair.CleanReturnAsync();
    }
}
