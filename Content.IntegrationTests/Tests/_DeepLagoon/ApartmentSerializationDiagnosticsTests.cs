#nullable enable annotations
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.EntitySerialization;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Player;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentSerializationDiagnosticsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task SmallApartmentCapturePreservesSnapshotWithBoundedDiagnostics()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var outside = await pair.CreateTestMap();
        ApartmentSnapshotCapture plain = null!, detailed = null!, warm = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var cfg = pair.Server.CfgMan;
            cfg.SetCVar(ApartmentCVars.Enabled, true);
            cfg.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var oldDetail = cfg.GetCVar(Robust.Shared.CVars.PerformanceLogLevel);
            var em = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            var homeSystem = em.System<ApartmentSystem>();
            var body = em.SpawnEntity("MobHuman", outside.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(homeSystem.Enter(session, session.UserId, out var message), Is.True, message);
            var home = homeSystem.Instances[session.UserId];
            var method = typeof(ApartmentSystem).GetMethod("CaptureApartment", Private)!;
            try
            {
                ApartmentSnapshotCapture Capture(bool diagnostics, string label)
                {
                    cfg.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, diagnostics ? 2 : 0);
                    var args = new object?[] { home, "capture", null };
                    var tuple = method.Invoke(homeSystem, args)!;
                    var count = (int) tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
                    Assert.That(count, Is.EqualTo(59), "Native small-apartment fixture; visitor/gear are excluded.");
                    if (!diagnostics) Assert.That(args[2], Is.Null);
                    else
                    {
                        var detail = args[2]!;
                        var report = (string) detail.GetType().GetMethod("Describe")!.Invoke(detail, null)!;
                        Assert.That((int) detail.GetType().GetField("_count", Private)!.GetValue(detail)!, Is.EqualTo(8));
                        Assert.That(((System.Array) detail.GetType().GetField("_stages", Private)!.GetValue(detail)!).Length, Is.EqualTo(8));
                        foreach (var stage in new[] { "exclusions", "before_hooks", "serializer_ctor", "identity", "recursive", "snapshot_build", "after_hooks", "round_end" })
                            Assert.That(report, Does.Contain(stage + "_thread_bytes="));
                        TestContext.WriteLine(label + " " + report);
                    }
                    return (ApartmentSnapshotCapture) tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
                }

                detailed = Capture(true, "detailed-first");
                plain = Capture(false, "plain");
                warm = Capture(true, "warm");

                // Compare the complete native snapshot through both provider implementations.
                // The internal constructor is test-only access; the public constructor ABI is unchanged.
                var constructor = typeof(EntitySerializer).GetConstructor(Private, null,
                    new[] { typeof(IDependencyCollection), typeof(SerializationOptions), typeof(bool) }, null)!;
                MappingDataNode? baseline = null;
                foreach (var indexed in new[] { true, false })
                {
                    var before = System.GC.GetAllocatedBytesForCurrentThread();
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var serializer = (EntitySerializer) constructor.Invoke(new object[] {
                        em.EntitySysManager.DependencyCollection, SerializationOptions.Default, indexed });
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
                    var nodeLength = ((System.Array) typeof(SerializationManager.SerializerProvider)
                        .GetField("_typeNodeSerializersArray", Private)!.GetValue(serializer.SerializerProvider)!).Length;
                    var typeLength = ((System.Array) typeof(SerializationManager.SerializerProvider)
                        .GetField("_typeSerializersArray", Private)!.GetValue(serializer.SerializerProvider)!).Length;
                    if (!indexed) Assert.That((nodeLength, typeLength), Is.EqualTo((0, 0)));
                    serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool allowed) =>
                        allowed &= !em.HasComponent<ActorComponent>(entity.Owner);
                    serializer.SerializeEntityRecursive(home.Grid);
                    Assert.That(serializer.EntityData.Count, Is.EqualTo(59));
                    var node = serializer.Write();
                    node.Get<MappingDataNode>("meta").Remove("time");
                    if (baseline == null) baseline = node;
                    else Assert.That(node.Equals(baseline), Is.True, "Compact context must preserve native map IDs, components and references.");
                    TestContext.WriteLine($"provider indexed={indexed} ctor_ms={elapsed:F3} ctor_thread_bytes={bytes} node_array={nodeLength} type_array={typeLength}");
                }
            }
            finally { cfg.SetCVar(Robust.Shared.CVars.PerformanceLogLevel, oldDetail); }
        });

        // The real pure formatter is deliberately exercised on a worker, never under the game context.
        var texts = await Task.Run(() => new[] { plain, detailed, warm }.Select(capture =>
        {
            var formatted = typeof(ApartmentSnapshotCapture).GetMethod("Format", Private)!.Invoke(capture, null)!;
            return (string) formatted.GetType().GetProperty("Current")!.GetValue(formatted)!;
        }).ToArray());
        MappingDataNode Parse(string text)
        {
            using var reader = new StringReader(text);
            var node = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
            node.Get<MappingDataNode>("meta").Remove("time");
            return node;
        }
        Assert.That(Parse(texts[1]).Equals(Parse(texts[0])), Is.True, "Diagnostics must not change the complete native snapshot.");
        Assert.That(Parse(texts[2]).Equals(Parse(texts[0])), Is.True);
        // An opt-in diagnostic run can keep the host alive for EventPipe rundown.
        // Ordinary regression executions do not wait.
        var traceHold = System.Math.Clamp(TestContext.Parameters.Get("SerializationTraceHoldSeconds", 0), 0, 90);
        if (traceHold > 0)
            await Task.Delay(System.TimeSpan.FromSeconds(traceHold));
        await pair.CleanReturnAsync();
    }
}
