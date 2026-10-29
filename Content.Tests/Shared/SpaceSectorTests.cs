using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class SpaceSectorTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Test]
    public void SeedStreamsAreStableAndSeparatePlanetAppearanceFromRegionCount()
    {
        var random = new SectorRandom(1);
        Assert.That(random.Next(), Is.EqualTo(270369u));
        Assert.That(random.Next(), Is.EqualTo(67634689u));
        var profile = new SectorProfilePrototype();
        var first = SectorGenerator.Generate("expedition/42", 713u, profile);
        var repeated = SectorGenerator.Generate("expedition/42", 713u, profile);
        Assert.That(JsonSerializer.Serialize(first, Json), Is.EqualTo(JsonSerializer.Serialize(repeated, Json)));
        profile.RegionCount = 60;
        var dense = SectorGenerator.Generate("expedition/42", 713u, profile);
        Assert.That(JsonSerializer.Serialize(first.Planets, Json), Is.EqualTo(JsonSerializer.Serialize(dense.Planets, Json)));
        Assert.That(SectorGenerator.Generate("expedition/42", 714u, profile).Planets[0].AppearanceSeed,
            Is.Not.EqualTo(first.Planets[0].AppearanceSeed));
    }

    [TestCase(1u)]
    [TestCase(713u)]
    [TestCase(uint.MaxValue)]
    public void GeneratedRegionsRespectBoundsAndBodyClearance(uint seed)
    {
        var profile = new SectorProfilePrototype();
        var sector = SectorGenerator.Generate("test", seed, profile);
        Assert.That(sector.Regions.Count, Is.InRange(1, profile.RegionCount));
        Assert.That(sector.Planets.Select(p => p.Id).Distinct().Count(), Is.EqualTo(sector.Planets.Count));
        foreach (var region in sector.Regions)
        {
            var position = new Vector2(region.X, region.Y);
            Assert.That(position.Length(), Is.InRange(profile.InnerRadius - 0.1f, profile.OuterRadius + 0.1f));
            foreach (var planet in sector.Planets)
                Assert.That(Vector2.Distance(position, new Vector2(planet.X, planet.Y)), Is.GreaterThan(region.Radius + 800f));
        }
    }

    [Test]
    public void InvalidProfileCannotProduceUnboundedWork()
    {
        Assert.Throws<ArgumentException>(() => SectorGenerator.Generate("bad", 1, new SectorProfilePrototype { RegionCount = 1000000 }));
        Assert.Throws<ArgumentException>(() => SectorGenerator.Generate("bad", 1, new SectorProfilePrototype { OuterRadius = float.NaN }));
        Assert.Throws<ArgumentException>(() => SectorGenerator.Generate("bad", 1, new SectorProfilePrototype { Kinds = new() }));
    }

    [Test]
    public void SweptIntersectionFindsThinEllipsesWhenBothEndpointsAreOutside()
    {
        var region = new SpaceRegion { Radius = 100, Aspect = 0.3f, Rotation = 0.7f };
        Assert.That(SpaceRegionGeometry.Contains(region, Vector2.Zero), Is.True);
        Assert.That(SpaceRegionGeometry.Intersects(region, new Vector2(-1000, 0), new Vector2(1000, 0)), Is.True);
        Assert.That(SpaceRegionGeometry.Intersects(region, new Vector2(-1000, 500), new Vector2(1000, 500)), Is.False);
        for (var i = 0; i < SpaceRegionGeometry.ContourSegments; i++)
            Assert.That(SpaceRegionGeometry.Local(region, SpaceRegionGeometry.ContourPoint(region, i)).Length(), Is.EqualTo(1f).Within(0.00001f));
    }

    [Test]
    public void SpatialIndexFindsTheSameIntersectionsAsBruteForceIncludingLongTeleports()
    {
        var regions = SectorGenerator.Generate("test", 45, new SectorProfilePrototype()).Regions;
        var index = new SpaceRegionIndex(regions);
        var random = new SectorRandom(123);
        var candidates = new HashSet<SpaceRegion>();
        for (var i = 0; i < 250; i++)
        {
            var start = new Vector2(random.Range(-35000, 35000), random.Range(-35000, 35000));
            var end = new Vector2(random.Range(-35000, 35000), random.Range(-35000, 35000));
            index.Query(start, end, candidates);
            var actual = candidates.Where(r => SpaceRegionGeometry.Intersects(r, start, end)).Select(r => r.Id);
            var expected = regions.Where(r => SpaceRegionGeometry.Intersects(r, start, end)).Select(r => r.Id);
            Assert.That(actual, Is.EquivalentTo(expected));
        }
    }

    [Test]
    public void RouteEstimateCountsExposureButFtlChecksOnlyEndpoints()
    {
        var regions = new List<SpaceRegion>
        {
            new() { Id = "dust", Radius = 100, Intensity = 1, Kind = SpaceRegionKind.Dust },
            new() { Id = "plasma", Radius = 100, Intensity = 1, Kind = SpaceRegionKind.Plasma },
        };
        var crossing = SpaceRouteEvaluator.Evaluate(new Vector2(-200, 0), new Vector2(200, 0), regions);
        Assert.That(crossing.DustDistance, Is.EqualTo(200f).Within(0.001f));
        Assert.That(crossing.PlasmaAtDeparture || crossing.PlasmaAtArrival, Is.False);
        var arrival = SpaceRouteEvaluator.Evaluate(new Vector2(-200, 0), Vector2.Zero, regions);
        Assert.That(arrival.PlasmaAtArrival, Is.True);
    }

    [Test]
    public void AppearanceGenerationDoesNotMutateSharedPrototypeOrAnotherPlanet()
    {
        var template = new PlanetTypePrototype
        {
            Name = "test", Shader = "RockyPlanet", EarthMass = 1f, Palette = "PaletteHabitableEarth",
            CustomData = new PlanetCustomValues { Floats = new() { ["terrainScale"] = 1f, ["macroFrequency"] = 3.75f } },
        };
        var first = new Planet(template, null, Vector2.Zero);
        var second = new Planet(template, null, Vector2.Zero);
        PlanetAppearanceGenerator.Apply(first, 1);
        PlanetAppearanceGenerator.Apply(second, 2);
        Assert.That(template.CustomData.Floats["terrainScale"], Is.EqualTo(1f));
        Assert.That(first.CustomData.Floats["terrainScale"], Is.Not.EqualTo(second.CustomData.Floats["terrainScale"]));
        var repeated = new Planet(template, null, Vector2.Zero);
        PlanetAppearanceGenerator.Apply(repeated, 1);
        Assert.That(repeated.Rotation, Is.EqualTo(first.Rotation));
        Assert.That(repeated.CustomData.Floats, Is.EquivalentTo(first.CustomData.Floats));
    }
}
