using Robust.Client.Utility;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class ClothingHairOcclusionTests
{
    [Test]
    public void LowerContourClipsTopSidesAndHolesButKeepsTheHairBelow()
    {
        using var hat = new Image<Rgba32>(32, 32);
        for (var y = 3; y <= 8; y++)
        for (var x = 12; x <= 19; x++) hat[x, y] = new Rgba32(255, 255, 255, 255);
        hat[15, 9] = hat[15, 10] = new Rgba32(255, 255, 255, 255);
        hat[14, 6] = default; // A transparent hole must not expose hair inside the hat.
        using var map = ClothingHairOcclusionMap.Build(hat, 32, 32);
        for (var y = 0; y <= 8; y++)
        for (var x = 0; x < 32; x++) Assert.That(map[x, y].A, Is.Zero, $"Above/beside hat: {x},{y}");
        Assert.That(map[15, 10].A, Is.Zero);
        Assert.That(map[12, 9].A, Is.EqualTo(255), "Follow each column's lower contour.");
        Assert.That(map[8, 9].A, Is.Zero, "Wide hair cannot escape beside the hat.");
        for (var y = 11; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            Assert.That(map[x, y], Is.EqualTo(new Rgba32((byte) x, (byte) y, 128, 255)),
                "Below the hat, preserve original pixel sampling and brightness.");
        }
        Assert.That(hat[14, 6].A, Is.Zero, "Do not mutate shared clothing assets.");
    }

    [Test]
    public void EmptyHeadwearDoesNotHideHairAndPaddedCoordinatesAgree()
    {
        using var hat = new Image<Rgba32>(32, 32);
        hat[16, 2] = new Rgba32(255, 255, 255, 127);
        using var empty = ClothingHairOcclusionMap.Build(hat, 32, 32);
        foreach (var pixel in empty.GetPixelSpan()) Assert.That(pixel.A, Is.EqualTo(255));
        hat[16, 2] = new Rgba32(255, 255, 255, 255);
        using var padded = new Image<Rgba32>(96, 96);
        padded[48, 34] = hat[16, 2];
        using var original = ClothingHairOcclusionMap.Build(hat, 32, 32);
        using var shifted = ClothingHairOcclusionMap.Build(padded, 32, 32, 32);
        Assert.That(shifted.GetPixelSpan().ToArray(), Is.EqualTo(original.GetPixelSpan().ToArray()));
    }
}