using System.IO;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class ClothingFaceFitTests
{
    [TestCase("Clothing/Head/Hats/beret_engineering.rsi/equipped-HELMET.png")]
    [TestCase("Clothing/Head/Helmets/security.rsi/equipped-HELMET.png")]
    public void RealOpenHeadwearDoesNotStretchOverTheMouth(string path)
    {
        for (var direction = 0; direction < 4; direction++)
        {
            using var reference = Read("Mobs/Species/Human/parts.rsi/head_m.png", direction);
            using var face = Read("_DV/Mobs/Species/Vulpkanin/parts.rsi/head_m.png", direction);
            using var garment = Read(path, direction);
            using var map = ClothingFaceFitMap.Build(reference, face, garment, null, 16);
            Assert.That(map, Is.Null, $"{path}/{direction} leaves the mouth uncovered.");
        }
    }

    [Test]
    public void OnlyCoveredLowerFaceExpandsAndOriginalAssetsStayUnchanged()
    {
        using var head = Box(12, 3, 19, 10);
        using var face = Box(12, 3, 24, 10);
        using var mask = Box(11, 6, 20, 11);
        using var cap = Box(11, 1, 20, 4);
        using var map = ClothingFaceFitMap.Build(head, face, mask, null, 16);
        Assert.That(map, Is.Not.Null);
        Assert.That(map[24 + 16, 8 + 16].A, Is.EqualTo(255));
        Assert.That(map[24 + 16, 8 + 16].R, Is.LessThanOrEqualTo(20));
        Assert.That(mask[24, 8].A, Is.Zero);
        Assert.That(face[24, 8].A, Is.EqualTo(255));
        using var unchangedCap = ClothingFaceFitMap.Build(head, face, cap, null, 16);
        Assert.That(unchangedCap, Is.Null, "A hat above the mouth must not acquire face volume.");
        using var unchangedHead = ClothingFaceFitMap.Build(head, head, mask, null, 16);
        Assert.That(unchangedHead, Is.Null, "The standard head needs no additional deformation.");
    }

    [TestCase("_DV/Mobs/Species/Vulpkanin/parts.rsi/head_m.png")]
    [TestCase("_DV/Mobs/Species/Feroxi/parts.rsi/head_m.png")]
    [TestCase("_Goobstation/Mobs/Species/Tajaran/parts.rsi/head_m.png")]
    public void RealMasksAndRigidHelmetsFollowNativeFaces(string headPath)
    {
        var changed = 0;
        foreach (var path in new[]
        {
            "Clothing/Mask/gas.rsi/equipped-MASK.png",
            "Clothing/Mask/medical.rsi/equipped-MASK.png",
            "Clothing/Head/Hardsuits/engineering.rsi/off-equipped-HELMET.png",
        })
        for (var direction = 0; direction < 4; direction++)
        {
            using var reference = Read("Mobs/Species/Human/parts.rsi/head_m.png", direction);
            using var face = Read(headPath, direction);
            if (headPath.Contains("Feroxi"))
            {
                using var snout = Read("_DV/Mobs/Customization/Feroxi/snout_markings.rsi/feroxi-snout.png", direction);
                for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                    if (snout[x, y].A >= 128) face[x, y] = snout[x, y];
            }
            using var garment = Read(path, direction);
            using var map = ClothingFaceFitMap.Build(reference, face, garment, null, 16);
            if (map == null) continue;
            changed++;
            var drawn = 0;
            for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var pixel = map[x, y];
                if (pixel.A == 0) continue;
                Assert.That(pixel.R, Is.LessThan(32));
                Assert.That(pixel.G, Is.LessThan(32));
                if (garment[pixel.R, pixel.G].A >= 128) drawn++;
            }
            Assert.That(drawn, Is.GreaterThan(0), $"{path}/{direction} disappeared.");
        }
        Assert.That(changed, Is.GreaterThan(0), $"No equipment adapted to {headPath}.");
    }

    [Test]
    public void AppearanceSnoutChangesTheMapEvenOnTheSameHead()
    {
        var changed = 0;
        for (var direction = 0; direction < 4; direction++)
        {
            using var head = Read("Mobs/Species/Human/parts.rsi/head_m.png", direction);
            using var face = head.Clone();
            using var snout = Read("_DV/Mobs/Customization/Vulpkanin/head_markings.rsi/muzzle.png", direction);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                if (snout[x, y].A >= 128) face[x, y] = snout[x, y];
            using var garment = Read("Clothing/Mask/gas.rsi/equipped-MASK.png", direction);
            using var map = ClothingFaceFitMap.Build(head, face, garment, null, 16);
            if (map != null) changed++;
        }
        Assert.That(changed, Is.GreaterThan(0), "An appearance snout must affect fitting independently of species.");
    }

    private static Image<Rgba32> Box(int left, int top, int right, int bottom)
    {
        var image = new Image<Rgba32>(32, 32);
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++) image[x, y] = new Rgba32(255, 255, 255, 255);
        return image;
    }

    private static Image<Rgba32> Read(string path, int direction)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Textures")))
            directory = directory.Parent;
        Assert.That(directory, Is.Not.Null);
        using var image = Image.Load<Rgba32>(Path.Combine(directory.FullName, "Resources", "Textures", path));
        var columns = image.Width / 32;
        return image.Clone(p => p.Crop(new Rectangle(direction % columns * 32, direction / columns * 32, 32, 32)));
    }
}
