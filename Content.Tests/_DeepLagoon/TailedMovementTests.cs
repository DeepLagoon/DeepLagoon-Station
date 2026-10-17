using System;
using System.Numerics;
using System.Reflection;
using Content.Shared._Exodus.Tailed;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class TailedMovementTests
{
    private static readonly Func<TailedEntityComponent, Vector2, Vector2, Vector2> Calculate =
        typeof(TailedEntitySystem).GetMethod("CalculateSegmentVelocity", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<TailedEntityComponent, Vector2, Vector2, Vector2>>();

    [TestCase(0f, -1f, -5f)]
    [TestCase(0f, 0f, 0f)]
    [TestCase(0.5f, 1f, -5f)]
    [TestCase(2f, -1f, 10f)]
    [TestCase(1f, 0.2f, 1f)]
    public void SegmentsRecoverFromOverlapAndPreserveSpacingRules(float previousX, float targetX, float expectedX)
    {
        var result = Calculate(new TailedEntityComponent(), new Vector2(previousX, 0), new Vector2(targetX, 0));
        Assert.That(float.IsFinite(result.X) && float.IsFinite(result.Y), Is.True);
        Assert.That(result, Is.EqualTo(new Vector2(expectedX, 0)));
    }
}
