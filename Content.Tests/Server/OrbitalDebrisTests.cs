using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Robust.Shared.Maths;
using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Server._DeepLagoon.SpaceEnvironment;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.Server;

[TestFixture]
public sealed class OrbitalDebrisTests
{
    [TestCase(0f, 2)]
    [TestCase(8000f, 4)]
    [TestCase(32000f, 10)]
    [TestCase(float.PositiveInfinity, 10)]
    public void DensityIncreasesWithRemotenessAndIsBounded(float distance, int expected)
        => Assert.That(OrbitalDebrisSystem.DesiredCount(distance), Is.EqualTo(expected));

    [Test]
    public void AnotherObserverAndAnotherMapAreHandledIndependently()
    {
        var map = new EntityUid(1);
        var first = new OrbitalDebrisSystem.Observer(map, Vector2.Zero, 50);
        var second = new OrbitalDebrisSystem.Observer(map, new Vector2(500, 0), 50);
        Assert.That(OrbitalDebrisSystem.Visible(first, map, second.Position), Is.False);
        Assert.That(OrbitalDebrisSystem.Visible(second, map, second.Position), Is.True);
        Assert.That(OrbitalDebrisSystem.Visible(second, new EntityUid(2), second.Position), Is.False);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(40)]
    [TestCase(101)]
    public void PoolSharesAreEvenAndNeverExceedSectorOrPersonalCaps(int players)
    {
        var shares = Enumerable.Range(0, players).Select(i => OrbitalDebrisSystem.FairShare(players, i)).ToArray();
        Assert.That(shares.Sum(), Is.LessThanOrEqualTo(100));
        Assert.That(shares.Max() - shares.Min(), Is.LessThanOrEqualTo(1));
        Assert.That(shares.All(x => x is >= 0 and <= 10), Is.True);
    }

    [TestCase(10f)]
    [TestCase(100f)]
    [TestCase(300f)]
    public void FastTravelSpawnsAheadWithAtLeastEightSecondsBeforeTheHorizon(float speed)
    {
        var velocity = Vector2.Normalize(new Vector2(3, 4)) * speed;
        var observer = new OrbitalDebrisSystem.Observer(new EntityUid(1), new Vector2(500, -900), 80, velocity);
        foreach (var lateral in new[] { -1f, 0f, 1f })
        {
            var position = OrbitalDebrisSystem.ForecastSpawn(observer, lateral, 0, 0);
            var lead = Vector2.Dot(position - observer.Position, Vector2.Normalize(velocity));
            Assert.That((lead - observer.Range - 16f) / (speed + 8f), Is.GreaterThanOrEqualTo(7.999f));
            Assert.That(OrbitalDebrisSystem.Visible(observer, observer.Map, position), Is.False);
        }
    }

    [TestCase(0f)]
    [TestCase(12f)]
    [TestCase(100f)]
    public void PlannedOrbitsReachThePredictedNormalViewport(float velocity)
    {
        var sector = new SectorDefinition { Planets = new() { new SectorPlanet { X = 10000 } },
            Regions = new() { new SpaceRegion { X = 5000, Radius = 2000, Kind = SpaceRegionKind.Debris } } };
        var observer = new OrbitalDebrisSystem.Observer(new Robust.Shared.GameObjects.EntityUid(1), new Vector2(5000, 0), 48.25f,
            new Vector2(velocity, 0));
        for (uint seed = 1; seed <= 100; seed++)
        {
            var plan = OrbitalDebrisSystem.PlanEncounter(sector, observer, seed, 0.8f, 1f, 0.7f);
            var offset = plan.Position - plan.Anchor;
            var arrival = plan.Anchor + OrbitalDebrisSystem.RotateOrbit(offset, plan.Direction * plan.Speed / offset.Length() * plan.ArrivalSeconds);
            var eyeAtArrival = observer.Position + observer.Velocity * plan.ArrivalSeconds;
            Assert.That(Vector2.Distance(arrival, plan.Target), Is.LessThan(0.01f));
            Assert.That(Vector2.Distance(arrival, eyeAtArrival), Is.LessThan(OrbitalDebrisSystem.NormalViewHalf(observer) * 0.4f));
        }
    }

    [Test]
    public void CentralFieldDoesNotTrapJunkOnAnInvisibleTinyCircle()
    {
        var sector = new SectorDefinition { Regions = new() { new SpaceRegion { Radius = 500, Kind = SpaceRegionKind.Debris } } };
        var observer = new OrbitalDebrisSystem.Observer(new Robust.Shared.GameObjects.EntityUid(1), Vector2.Zero, 48.25f);
        var plan = OrbitalDebrisSystem.PlanEncounter(sector, observer, 713, 0, 0, 0);
        Assert.That(Vector2.Distance(plan.Anchor, plan.Target), Is.GreaterThanOrEqualTo(256f));
        Assert.That(Vector2.Distance(plan.Target, observer.Position), Is.LessThan(OrbitalDebrisSystem.NormalViewHalf(observer)));
    }

    [Test]
    public void FieldsAreRichAndOpenSpaceIsSparse()
    {
        var sector = new SectorDefinition { Planets = new() { new SectorPlanet { X = 5000 } },
            Regions = new() { new SpaceRegion { X = 10000, Radius = 500, Kind = SpaceRegionKind.Debris } } };
        var open = OrbitalDebrisSystem.Habitat(sector, Vector2.Zero);
        var planet = OrbitalDebrisSystem.Habitat(sector, new Vector2(5000, 0));
        var field = OrbitalDebrisSystem.Habitat(sector, new Vector2(10000, 0));
        Assert.That(open.Target, Is.EqualTo(1));
        Assert.That(open.Interval, Is.EqualTo(90));
        Assert.That(planet.Target, Is.EqualTo(3));
        Assert.That(field.Target, Is.EqualTo(10));
        Assert.That(field.MaxExtent, Is.EqualTo(10));
        Assert.That(field.Rich, Is.True);
        Assert.That(field.Interval, Is.LessThan(open.Interval));
    }

    [Test]
    public void LargeJunkShapesRemainConnectedAndBounded()
    {
        for (uint seed = 1; seed <= 100; seed++)
        {
            var random = new SectorRandom(seed);
            var shape = OrbitalDebrisSystem.JunkShape(10, 10, 36, ref random);
            Assert.That(shape.Count, Is.InRange(19, 36));
            Assert.That(shape.Distinct().Count(), Is.EqualTo(shape.Count));
            Assert.That(shape.Max(p => p.X), Is.EqualTo(9));
            Assert.That(shape.Max(p => p.Y), Is.EqualTo(9));
            var reached = new HashSet<Vector2i> { shape[0] };
            for (var pass = 0; pass < shape.Count; pass++)
                foreach (var cell in shape)
                    if (reached.Contains(cell + new Vector2i(1, 0)) || reached.Contains(cell + new Vector2i(-1, 0))
                        || reached.Contains(cell + new Vector2i(0, 1)) || reached.Contains(cell + new Vector2i(0, -1))) reached.Add(cell);
            Assert.That(reached.Count, Is.EqualTo(shape.Count));
        }
    }

    [TestCase(1f)]
    [TestCase(1.5f)]
    [TestCase(2f)]
    public void PlanetPortraitNeverShrinksBelowThirtyTwoUiPixels(float uiScale)
    {
        foreach (var scale in new[] { 0.000001f, 0.001f, 0.1f, 1f, 100f })
            Assert.That(SpaceNavigationDraw.DisplayRadius(10f * scale, uiScale, true) * 2f / uiScale,
                Is.InRange(32f, 140f));
    }
}
