using System;
using SixLabors.ImageSharp.PixelFormats;
using Content.Client._DeepLagoon.AmbientOcclusion;
using ContentDrawDepth = Content.Shared.DrawDepth.DrawDepth;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class AmbientOcclusionTests
{
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
