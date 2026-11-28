using System.Linq;
using Content.Shared._DeepLagoon.Voyages;
using NUnit.Framework;

namespace Content.Tests.Client;

public sealed class VoyageVisibilityTests
{
    [Test]
    public void FogRevealsOnlyOneFrontierHopAndExpandsAfterExploration()
    {
        var graph = new VoyageRouteModel();
        graph.Systems.Clear(); graph.Lanes.Clear();
        graph.Systems.AddRange(new[] {
            new VoyageDestination("home", "Home", default, "", "", true, []),
            new VoyageDestination("near", "Near", default, "", "", false, []),
            new VoyageDestination("far", "Far", default, "", "", false, []),
            new VoyageDestination("edge", "Edge", default, "", "", false, []) });
        graph.Lanes.AddRange(new[] { new VoyageLane("home", "near", 1), new VoyageLane("near", "far", 1), new VoyageLane("far", "edge", 1) });
        Assert.That(VoyageVisibility.VisibleSystems(graph), Is.EquivalentTo(new[] { "home", "near" }));
        graph.Systems[1] = graph.Systems[1] with { Known = true };
        Assert.That(VoyageVisibility.VisibleSystems(graph), Is.EquivalentTo(new[] { "home", "near", "far" }));
        Assert.That(VoyageVisibility.VisibleSystems(graph, "far"), Does.Contain("edge"));
    }
}
