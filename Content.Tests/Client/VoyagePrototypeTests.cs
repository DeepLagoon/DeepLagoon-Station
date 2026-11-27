using System;
using Content.Shared._DeepLagoon.Voyages;
using System.Linq;
using Content.Client._DeepLagoon.Voyages;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class VoyagePrototypeTests
{
    [TestCase(0)]
    [TestCase(213)]
    [TestCase(932941)]
    [TestCase(-2147483648)]
    public void GeneratedSystemsKeepEnoughSpaceForSeparateMarkers(int seed)
    {
        var model = new VoyageRouteModel(); model.SetGalaxy(seed, Guid.Empty);
        foreach (var node in model.Systems)
            foreach (var other in model.Systems.Where(o => o.Id != node.Id))
                Assert.That(System.Numerics.Vector2.Distance(node.Position, other.Position), Is.GreaterThanOrEqualTo(259.99f), node.Id + " overlaps " + other.Id);
    }
    [Test]
    public void OrganicGalaxyHasIrregularCellsAndPreservesStableRouteIdentifiers()
    {
        var model = new VoyageRouteModel(); model.SetGalaxy(213, Guid.NewGuid());
        var unknown = model.Systems.Where(n => n.Id.StartsWith("sector-")).ToArray();
        foreach (var ring in unknown.GroupBy(n => n.Id.Split('-')[1]))
            Assert.That(ring.Max(n => n.Position.Y) - ring.Min(n => n.Position.Y), Is.GreaterThan(200), "Unknown sectors must not form horizontal rows.");
        foreach (var node in model.Systems)
        {
            var cell = VoyageGalaxy.SectorCell(model.Systems, node.Id);
            Assert.That(cell.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(cell.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), Is.True);
        }
        Assert.That(model.Lanes.Count, Is.EqualTo(242));
        Assert.That(SectorResearchDefinition.FlightTime(30, 5.5f, 20f, false), Is.EqualTo(4 * SectorResearchDefinition.FlightTime(30, 5.5f, 20f)));
    }
    [Test]
    public void PlanningUsesOnlyChartedRoutesAndSurveyUnlocksTheDestinationAfterArrival()
    {
        var model = new VoyagePrototype { Speed = 32 };
        Assert.That(model.Path(VoyagePrototype.Home, "nereid"), Is.EqualTo(new[] { VoyagePrototype.Home, "nereid" }));
        Assert.That(model.Minutes(model.Path(VoyagePrototype.Home, "nereid")), Is.EqualTo(16));
        Assert.That(model.Path(VoyagePrototype.Home, "echo"), Is.Empty);
        var crew = model.Hire("cargo")!;
        Assert.That(model.Dispatch("echo", false, crew.Id), Is.False);
        Assert.That(model.Dispatch("echo", true, crew.Id), Is.True);
        var survey = model.Flights.Last();
        Assert.That(survey.Stops, Is.EqualTo(new[] { VoyagePrototype.Home, "helios", "echo" }));
        Assert.That(model.System("echo").Known, Is.False);
        for (var i = 0; i < 260; i++) model.Advance(.25);
        Assert.That(model.System("echo").Known, Is.True);
        Assert.That(model.Path(VoyagePrototype.Home, "echo"), Is.Not.Empty);
        Assert.That(survey.Progress(model.Clock), Is.EqualTo(1));
        Assert.That(model.Position(survey), Is.EqualTo(model.System("echo").Position));
    }

    [Test]
    public void AnimationMovesAlongTheRouteAndPauseDoesNotAdvanceIt()
    {
        var model = new VoyagePrototype();
        var flight = model.Flights[0];
        var start = model.Position(flight);
        model.Advance(.25);
        Assert.That(model.Position(flight), Is.Not.EqualTo(start));
        var moving = model.Position(flight);
        model.Paused = true; model.Advance(.25);
        Assert.That(model.Position(flight), Is.EqualTo(moving));
        model.Paused = false;
        Assert.That(model.Dispatch(VoyagePrototype.Home, false, model.Hire("cargo")!.Id), Is.False);
        for (var i = 0; i < 20; i++) model.Dispatch("nereid", false, model.Hire("cargo")!.Id);
        Assert.That(model.Flights.Count(f => f.Progress(model.Clock) < 1), Is.EqualTo(8));
    }
    [Test]
    public void HiringReservesAnAvailableCrewUntilArrivalAndUsesOnlyTheDemoBudget()
    {
        var model = new VoyagePrototype { Speed = 32 };
        Assert.That(model.Dispatch("nereid", false, 1), Is.False);
        Assert.That(model.Flights.All(f => !f.IsLocal), Is.True);
        var crew = model.Hire("survey")!;
        Assert.That(model.HiringBudget, Is.EqualTo(141000));
        Assert.That(model.Dispatch("echo", true, crew.Id), Is.True);
        Assert.That(model.IsBusy(crew), Is.True);
        Assert.That(model.Dispatch("nereid", false, crew.Id), Is.False);
        Assert.That(model.Flights.Last().IsLocal, Is.True);
        Assert.That(model.Flights.Last().Seconds, Is.EqualTo(1360));
        for (var i = 0; i < 180; i++) model.Advance(.25);
        Assert.That(model.IsBusy(crew), Is.False);
        Assert.That(model.Dispatch("nereid", false, crew.Id), Is.True);
        Assert.That(model.HiringBudget, Is.EqualTo(141000));
        while (model.HiringBudget >= 15000) Assert.That(model.Hire("express"), Is.Not.Null);
        var remaining = model.HiringBudget;
        Assert.That(model.Hire("express"), Is.Null);
        Assert.That(model.HiringBudget, Is.EqualTo(remaining));
        Assert.That(model.Hire("missing"), Is.Null);
    }

    [Test]
    public void ARealVoyageClockIgnoresPresentationSpeedAndPause()
    {
        var model = new VoyagePrototype(); model.UseServer();
        model.Paused = true; model.Speed = 32; model.Advance(.25);
        Assert.That(model.Clock, Is.EqualTo(.25));
        Assert.That(model.Crews, Is.Empty); Assert.That(model.Flights, Is.Empty);
    }

    [Test]
    public void GeneratedGalaxyIsConnectedVariedDeterministicAndCheaperAtDistance()
    {
        var model = new VoyageRouteModel(); model.SetGalaxy(123, System.Guid.Empty);
        Assert.That(model.Systems, Has.Count.EqualTo(116)); Assert.That(model.Lanes.Count, Is.GreaterThan(200));
        Assert.That(model.Lanes.Select(l => VoyageGalaxy.LaneKey(l.From, l.To)).Distinct().Count(), Is.EqualTo(model.Lanes.Count));
        var outer = model.Systems.Where(s => s.Id.StartsWith("sector-")).ToArray();
        Assert.That(outer.All(s => !s.Known), Is.True); Assert.That(outer.Select(s => s.PlanetType).Distinct().Count(), Is.GreaterThanOrEqualTo(5));
        var visited = new System.Collections.Generic.HashSet<string> { VoyageRouteModel.Home };
        while (true)
        {
            var before = visited.Count;
            foreach (var edge in model.Lanes) { if (visited.Contains(edge.From)) visited.Add(edge.To); if (visited.Contains(edge.To)) visited.Add(edge.From); }
            if (visited.Count == before) break;
        }
        Assert.That(visited.Count, Is.EqualTo(116));
        var again = new VoyageRouteModel(); again.SetGalaxy(123, System.Guid.Empty);
        Assert.That(again.Systems.Select(s => s.Position), Is.EqualTo(model.Systems.Select(s => s.Position)));
        again.SetGalaxy(456, System.Guid.NewGuid()); Assert.That(again.System("sector-7-0").Position, Is.Not.EqualTo(model.System("sector-7-0").Position));
        Assert.That(VoyageGalaxy.PriceFactor(model.System("sector-7-0").Position), Is.LessThan(VoyageGalaxy.PriceFactor(model.System("sector-0-0").Position)));
    }

    [Test]
    public void LostOwnedCrewDoesNotTurnItsMissionIntoAnotherPlayersFlight()
    {
        var model = new VoyagePrototype();
        model.Apply(new VoyageSnapshot { Origin = "Колосс", Clock = 100, Flights = [new(System.Guid.NewGuid(), true, System.Guid.NewGuid(),
            "Наёмники", [VoyageRouteModel.Home, "helios"], 1, 40, 80, false, true, false, "Колосс", Failed: true, Kind: 3, Combat: 2)] });
        Assert.That(model.Flights.Single().IsLocal, Is.True); Assert.That(model.Flights.Single().Progress(model.Clock), Is.EqualTo(1));
    }

}
