using Content.Server.DeviceLinking.Systems;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.DeviceLinking;

[TestFixture]
public sealed partial class DeviceLinkLifecycleTests
{
    [RegisterComponent]
    public sealed partial class DeviceLinkLifecycleProbeComponent : Component
    {
        public bool DeleteSourceOnLink;
        public bool RemoveSourceOnDisconnect;
    }

    public sealed class DeviceLinkLifecycleProbeSystem : EntitySystem
    {
        public int Disconnections;
        public override void Initialize()
        {
            SubscribeLocalEvent<DeviceLinkLifecycleProbeComponent, NewLinkEvent>((uid, comp, args) =>
            {
                if (comp.DeleteSourceOnLink) Del(args.Source);
            });
            SubscribeLocalEvent<DeviceLinkLifecycleProbeComponent, PortDisconnectedEvent>((uid, comp, args) =>
            {
                if (!comp.RemoveSourceOnDisconnect) return;
                Disconnections++;
                RemComp<DeviceLinkSourceComponent>(uid);
            });
        }
    }

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: DeviceLinkLifecycleSource
  components:
  - type: DeviceLinkSource
    ports: [Output]
  - type: DeviceLinkLifecycleProbe
- type: entity
  id: DeviceLinkLifecycleSink
  components:
  - type: DeviceLinkSink
    ports: [Toggle]
  - type: DeviceLinkLifecycleProbe
";

    [TestCase("delete-on-link")]
    [TestCase("remove-on-disconnect")]
    [TestCase("invalid-ports")]
    public async Task ReentrantCallbacksNeverLeaveBacklinks(string mode)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<DeviceLinkSystem>();
            var probe = entities.System<DeviceLinkLifecycleProbeSystem>();
            probe.Disconnections = 0;
            var source = entities.SpawnEntity("DeviceLinkLifecycleSource", map.GridCoords);
            var sink = entities.SpawnEntity("DeviceLinkLifecycleSink", map.GridCoords);
            entities.GetComponent<DeviceLinkLifecycleProbeComponent>(sink).DeleteSourceOnLink = mode == "delete-on-link";
            entities.GetComponent<DeviceLinkLifecycleProbeComponent>(source).RemoveSourceOnDisconnect = mode == "remove-on-disconnect";
            system.SaveLinks(null, source, sink, [("Output", mode == "invalid-ports" ? "On" : "Toggle")]);
            if (mode == "remove-on-disconnect")
            {
                system.RemoveSinkFromSource(source, sink);
                Assert.That(entities.HasComponent<DeviceLinkSourceComponent>(source), Is.False);
                Assert.That(probe.Disconnections, Is.EqualTo(1));
            }
            if (mode == "delete-on-link") Assert.That(entities.EntityExists(source), Is.False);
            Assert.That(entities.GetComponent<DeviceLinkSinkComponent>(sink).LinkedSources, Is.Empty);
            if (entities.TryGetComponent<DeviceLinkSourceComponent>(source, out var remaining))
                Assert.That(remaining.LinkedPorts, Is.Empty);
        });
        await pair.Server.WaitPost(() => pair.Server.System<SharedMapSystem>().DeleteMap(map.MapId));
        await pair.CleanReturnAsync();
    }
}
