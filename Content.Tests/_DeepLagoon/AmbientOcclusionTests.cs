using System;
using SixLabors.ImageSharp.PixelFormats;
using Content.Client._DeepLagoon.AmbientOcclusion;
using ContentDrawDepth = Content.Shared.DrawDepth.DrawDepth;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class AmbientOcclusionTests
{
    [TestCase(8f)]
    [TestCase(24f)]
    [TestCase(32f)]
    public void ContactsFadeBeforeLeavingDrawDistance(float range)
    {
        var fadeWidth = Math.Min(4f, range * 0.2f);
        Assert.That(AmbientOcclusionRange.GetOpacity(0, range), Is.EqualTo(1f));
        Assert.That(AmbientOcclusionRange.GetOpacity(range - fadeWidth, range), Is.EqualTo(1f).Within(0.00001f));
        Assert.That(AmbientOcclusionRange.GetOpacity(range - fadeWidth / 2, range), Is.EqualTo(0.5f).Within(0.00001f));
        Assert.That(AmbientOcclusionRange.GetOpacity(range - 0.01f, range), Is.LessThan(0.001f));
        Assert.That(AmbientOcclusionRange.GetOpacity(range, range), Is.Zero);
        Assert.That(AmbientOcclusionRange.GetOpacity(range + 1, range), Is.Zero);
    }

    [TestCase(256)]
    [TestCase(768)]
    [TestCase(2048)]
    public void BudgetFadeStartsBeforeOverflowAndKeepsNearbyContacts(int limit)
    {
        Assert.That(AmbientOcclusionRange.GetBudgetOpacity(0, limit), Is.EqualTo(1f));
        Assert.That(AmbientOcclusionRange.GetBudgetOpacity(limit * 3 / 4 - 1, limit), Is.EqualTo(1f));
        Assert.That(AmbientOcclusionRange.GetBudgetOpacity(limit * 7 / 8 - 1, limit), Is.EqualTo(0.5f));
        Assert.That(AmbientOcclusionRange.GetBudgetOpacity(limit - 1, limit), Is.Zero);
        Assert.That(AmbientOcclusionRange.GetBudgetOpacity(limit, limit), Is.Zero);
    }

    [Test]
    public void MovingAcrossBudgetBoundaryDoesNotRemoveAnOpaqueShadow()
    {
        const int limit = 768;
        var beforeCrossing = AmbientOcclusionRange.GetBudgetOpacity(limit - 2, limit);
        var atBoundary = AmbientOcclusionRange.GetBudgetOpacity(limit - 1, limit);
        var afterCrossing = AmbientOcclusionRange.GetBudgetOpacity(limit, limit);
        Assert.That(beforeCrossing - atBoundary, Is.LessThan(0.001f));
        Assert.That(atBoundary, Is.EqualTo(afterCrossing));
        for (var i = 0; i < limit; i++)
        {
            var previous = AmbientOcclusionRange.GetBudgetOpacity(i, limit);
            var next = AmbientOcclusionRange.GetBudgetOpacity(i + 1, limit);
            Assert.That(previous - next, Is.InRange(0f, 0.01f));
        }
    }

    [TestCase(ContentDrawDepth.FloorObjects)]
    [TestCase(ContentDrawDepth.Objects)]
    [TestCase(ContentDrawDepth.Mobs)]
    [TestCase(ContentDrawDepth.Overdoors)]
    public void PuddlesNeverCastContactShadows(ContentDrawDepth depth)
    {
        Assert.That(AmbientOcclusionSystem.ContactOverlay.CanCastContact(true, false, 1, false,
            (int)depth, puddle: true), Is.False);
        Assert.That(AmbientOcclusionSystem.ContactOverlay.CanCastContact(true, false, 1, false,
            (int)depth), Is.True);
    }

    [TestCase(1, 4, 1f)]
    [TestCase(1, 8, 1f)]
    [TestCase(2, 4, 1f)]
    [TestCase(2, 8, 1f)]
    [TestCase(1, 4, 3f)]
    [TestCase(1, 8, 3f)]
    [TestCase(2, 4, 3f)]
    [TestCase(2, 8, 3f)]
    public void WallCornersUseOnlyTheStrongestContact(int firstWall, int secondWall, float intensity)
    {
        const int size = AmbientOcclusionMask.Steps;
        var first = new Rgba32[size * size];
        var second = new Rgba32[size * size];
        var combined = new Rgba32[size * size];
        AmbientOcclusionMask.ComposeTile(first, size, size, 0, 0, firstWall, null, intensity);
        AmbientOcclusionMask.ComposeTile(second, size, size, 0, 0, secondWall, null, intensity);
        AmbientOcclusionMask.ComposeTile(combined, size, size, 0, 0, firstWall | secondWall, null, intensity);
        for (var i = 0; i < combined.Length; i++)
            Assert.That(combined[i].A, Is.EqualTo(Math.Max(first[i].A, second[i].A)));
    }
}
