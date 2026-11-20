using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Persistence;
using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ApartmentSnapshotWorkerTests
{
    [OneTimeSetUp]
    public void Sqlite()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }

    private static MappingDataNode Grid() => new()
    {
        { "meta", new MappingDataNode { { "entityCount", "4" }, { "time", "captured-before-edit" } } },
        { "orphans", new SequenceDataNode("1", "3") },
        { "nullspace", new SequenceDataNode("4") },
        { "entities", new SequenceDataNode(
            new MappingDataNode { { "proto", "Grid" }, { "entities", new SequenceDataNode(new MappingDataNode {
                { "uid", "1" }, { "literal", new ValueDataNode("null") }
            }) } },
            new MappingDataNode { { "proto", "Bin" }, { "entities", new SequenceDataNode(new MappingDataNode {
                { "uid", "2" }, { "components", new SequenceDataNode(
                    new MappingDataNode { { "type", "Storage" }, { "storedItems", new MappingDataNode { { "3", "saved placement" } } } },
                    new MappingDataNode { { "type", "ContainerContainer" }, { "containers", new MappingDataNode {
                        { "storage", new MappingDataNode { { "ents", new SequenceDataNode("3") } } }
                    } } }) }
            }) } },
            new MappingDataNode { { "proto", "Trash" }, { "entities", new SequenceDataNode(
                new MappingDataNode { { "uid", "3" } }, new MappingDataNode { { "uid", "4" } }) } }) }
    };

    private static string NativeYaml(MappingDataNode data)
    {
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }
    private static MappingDataNode Parse(string yaml)
    {
        using var reader = new StringReader(yaml);
        return (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
    }

    [Test]
    public async Task QueuedFormattingFreezesMetadataBeforeConcurrentEditAndDoesNotFormatOnCaller()
    {
        var path = Path.Combine(Path.GetTempPath(), "dl-yaml-worker-" + Guid.NewGuid().ToString("N") + ".db");
        var owner = Guid.NewGuid();
        try
        {
            using var store = new ApartmentLayoutStore(path);
            await store.SaveSnapshotAsync(owner, "home", new() { Grid = "previous" }, new() { Grid = "previous" });
            var queue = (SerialPersistenceQueue) typeof(ApartmentLayoutStore)
                .GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var blocked = queue.Enqueue(() => { entered.Set(); release.Wait(); });
            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                var capture = new ApartmentSnapshotCapture(Grid(), Array.Empty<string>(), Array.Empty<string>());
                var metadata = new ApartmentSnapshot { Layout = new() { Template = "home", Revision = 7, PurchasedPlacements = new() { "captured-paid" } },
                    MapFurniture = new() { new() { Baseline = new() { Id = "chair", X = 3 } } } };
                var receipt = store.SaveCapturedSnapshotAsync(owner, "home", capture, metadata);
                metadata.Layout.Revision = 99; metadata.MapFurniture[0].Baseline.X = 99;
                metadata.Layout.PurchasedPlacements.Add("later-paid");
                var lazy = typeof(ApartmentSnapshotCapture).GetField("_formatted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
                Assert.That(lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy), Is.False,
                    "Enqueue/copy must not trigger ToYaml on the caller before the worker runs.");
                Assert.That(receipt.IsCompleted, Is.False);
                Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("previous"), "WAL readers keep the last committed save.");
                release.Set(); await blocked;
                var result = await receipt;
                var saved = store.LoadSnapshot(owner, "home")!;
                Assert.That(saved.Grid, Is.EqualTo(NativeYaml(Grid())));
                Assert.That(saved.Layout.Revision, Is.EqualTo(7));
                Assert.That(saved.Layout.PurchasedPlacements, Is.EquivalentTo(new[] { "captured-paid" }));
                Assert.That(saved.MapFurniture.Single().Baseline.X, Is.EqualTo(3));
                Assert.That(result.YamlChars, Is.EqualTo(saved.Grid.Length));
                Assert.That(result.RoundEndYaml, Is.EqualTo(TimeSpan.Zero), "No trash reuses the very same current YAML.");
                await store.FinishRoundAsync(); await store.FlushAsync();
                Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo(saved.Grid));
            }
            finally { release.Set(); }
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Test]
    public async Task FormattingFailureKeepsPreviousSaveAndDeliveryWhileFollowingSnapshotDerivesRoundEndAndFlushes()
    {
        using var store = new ApartmentLayoutStore(":memory:");
        var owner = Guid.NewGuid(); var delivery = Guid.NewGuid();
        await store.SaveSnapshotAsync(owner, "home", new() { Grid = "previous" }, new() { Grid = "previous" });
        await store.BeginItemOrderAsync(owner, delivery, "test-item", 1);
        var bad = new ApartmentSnapshotCapture(new MappingDataNode { { "valid_current", "but no entity list" } }, new[] { "3" }, new[] { "2" });
        var failed = store.SaveCapturedSnapshotAsync(owner, "home", bad, new() { Layout = new() { Template = "home", Revision = 7 } }, delivery);
        var error = Assert.CatchAsync<Exception>(async () => await failed)!;
        Assert.That(error.Data["ApartmentSaveStage"], Is.EqualTo("round_end_build_worker"));
        Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("previous"));
        Assert.That(store.PendingItemOrder(owner)!.Delivered, Is.False);

        var rejectedDelivery = store.SaveCapturedSnapshotAsync(owner, "home", new ApartmentSnapshotCapture(Grid(), new[] { "3", "4" }, new[] { "2" }),
            new() { Layout = new() { Template = "home", Revision = 8 } }, Guid.NewGuid());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await rejectedDelivery);
        Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("previous"), "Failure after INSERT must roll the entire snapshot/delivery transaction back.");
        Assert.That(store.PendingItemOrder(owner)!.Delivered, Is.False);

        var capture = new ApartmentSnapshotCapture(Grid(), new[] { "3", "4" }, new[] { "2" });
        await store.SaveCapturedSnapshotAsync(owner, "home", capture, new() { Layout = new() { Template = "home", Revision = 8 } }, delivery);
        var current = store.LoadSnapshot(owner, "home")!;
        Assert.That(current.Grid, Is.EqualTo(NativeYaml(Grid())), "Current output must match the existing native emitter byte-for-byte.");
        Assert.That(current.Layout.Revision, Is.EqualTo(8));
        Assert.That((await store.BeginItemOrderAsync(owner, delivery, "test-item", 1)).Delivered, Is.True);
        Assert.That(Parse(current.Grid).Get<SequenceDataNode>("entities").Cast<MappingDataNode>()
            .SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()).Count(), Is.EqualTo(4));

        var finish = store.FinishRoundAsync(); await store.FlushAsync(); await finish;
        var clean = Parse(store.LoadSnapshot(owner, "home")!.Grid);
        Assert.That(clean.Get<MappingDataNode>("meta").Get<ValueDataNode>("entityCount").Value, Is.EqualTo("2"));
        var entities = clean.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()
            .SelectMany(group => group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>()).ToArray();
        Assert.That(entities.Select(entity => entity.Get<ValueDataNode>("uid").Value), Is.EquivalentTo(new[] { "1", "2" }));
        Assert.That(entities.Single(entity => entity.Get<ValueDataNode>("uid").Value == "1").Get<ValueDataNode>("literal").IsNull, Is.False);
        var components = entities.Single(entity => entity.Get<ValueDataNode>("uid").Value == "2").Get<SequenceDataNode>("components").Cast<MappingDataNode>().ToArray();
        Assert.That(components.Single(c => c.Get<ValueDataNode>("type").Value == "Storage").Get<MappingDataNode>("storedItems").Count, Is.Zero);
        Assert.That(components.Single(c => c.Get<ValueDataNode>("type").Value == "ContainerContainer")
            .Get<MappingDataNode>("containers").Get<MappingDataNode>("storage").Get<SequenceDataNode>("ents").Count, Is.Zero);
        Assert.That(clean.Get<SequenceDataNode>("orphans").Cast<ValueDataNode>().Select(n => n.Value), Is.EqualTo(new[] { "1" }));
        Assert.That(clean.Get<SequenceDataNode>("nullspace").Count, Is.Zero);
    }
}
