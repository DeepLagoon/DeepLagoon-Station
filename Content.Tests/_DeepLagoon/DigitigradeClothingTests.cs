using System.IO;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class DigitigradeClothingTests
{
    private const string Maps = "_White/Mobs/Species/displacement.rsi/";
    private const string HemMaps = "_DeepLagoon/Mobs/Digitigrade/displacement.rsi/";

    [TestCase("Vulpkanin", "_DV", false)]
    [TestCase("Vulpkanin", "_DV", true)]
    [TestCase("Feroxi", "_DV", false)]
    [TestCase("Feroxi", "_DV", true)]
    [TestCase("Tajaran", "_Goobstation", false)]
    [TestCase("Tajaran", "_Goobstation", true)]
    public void EachLegJoinsTheNativeTorso(string species, string folder, bool female)
    {
        for (var direction = 0; direction < 4; direction++)
        {
            using var torso = Read($"{folder}/Mobs/Species/{species}/parts.rsi/torso_{(female ? "f" : "m")}.png", direction);
            foreach (var part in new[] { "l_leg", "r_leg" })
            {
                using var leg = Read($"{folder}/Mobs/Species/{species}/parts.rsi/{part}.png", direction);
                var attached = false;
                for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    if (leg[x, y].A < 128) continue;
                    attached |= torso[x, y].A >= 128
                        || x > 0 && torso[x - 1, y].A >= 128
                        || x < 31 && torso[x + 1, y].A >= 128
                        || y > 0 && torso[x, y - 1].A >= 128
                        || y < 31 && torso[x, y + 1].A >= 128;
                }
                Assert.That(attached, Is.True,
                    $"{species}/{(female ? "female" : "male")}/{direction}/{part}: leg floats below the native torso.");
            }
        }
    }

    [TestCase("Vulpkanin", "_DV", false)]
    [TestCase("Vulpkanin", "_DV", true)]
    [TestCase("Feroxi", "_DV", false)]
    [TestCase("Feroxi", "_DV", true)]
    [TestCase("Tajaran", "_Goobstation", false)]
    [TestCase("Tajaran", "_Goobstation", true)]
    public void RealGarmentsPreserveHemsAndComposeWithBodyFit(string species, string folder, bool female)
    {
        var outfits = new[]
        {
            ("Clothing/Uniforms/Jumpsuit/engineering.rsi/equipped-INNERCLOTHING.png", false, false),
            ("Clothing/Uniforms/Jumpskirt/engineering.rsi/equipped-INNERCLOTHING.png", true, false),
            ("Clothing/Uniforms/Jumpskirt/long_dress.rsi/equipped-INNERCLOTHING.png", true, false),
            ("Clothing/Uniforms/Jumpskirt/victorian_black_dress.rsi/equipped-INNERCLOTHING.png", true, false),
            ("Clothing/OuterClothing/Coats/labcoat.rsi/equipped-OUTERCLOTHING.png", true, true),
            ("Clothing/OuterClothing/Hardsuits/engineering.rsi/equipped-OUTERCLOTHING.png", false, true),
        };
        using var preview = new Image<Rgba32>(64 * 8, 64 * (outfits.Length + 3));
        var changed = 0;
        for (var direction = 0; direction < 4; direction++)
        {
            using var body = Read($"{folder}/Mobs/Species/{species}/parts.rsi/torso_{(female ? "f" : "m")}.png", direction);
            foreach (var part in new[] { "head_" + (female ? "f" : "m"), "l_arm", "r_arm", "l_hand", "r_hand", "l_leg", "r_leg", "l_foot", "r_foot" })
            {
                using var limb = Read($"{folder}/Mobs/Species/{species}/parts.rsi/{part}.png", direction);
                Overlay(body, limb, 0, 0);
            }
            using var shoe = Read("Clothing/Shoes/Boots/laceups.rsi/equipped-FEET.png", direction);
            using var shoeDisplacement = Read(HemMaps + "shoes.png", direction);
            using var shoeMap = ClothingBodyFitMap.Build(body, [], shoe, shoeDisplacement, 16, true);
            using var fittedShoe = Render(shoe, shoeMap);
            Assert.That(Opaque(fittedShoe), Is.GreaterThan(0), "Shoes must not disappear.");
            using var limbs = new Image<Rgba32>(32, 32);
            foreach (var part in new[] { "l_leg", "r_leg", "l_foot", "r_foot" })
            {
                using var limb = Read($"{folder}/Mobs/Species/{species}/parts.rsi/{part}.png", direction);
                Overlay(limbs, limb, 0, 0);
            }
            var markingPath = species == "Tajaran" ? "_Goobstation/Mobs/Customization/Tajaran/overlays.rsi/"
                : $"_DV/Mobs/Customization/{species}/body_markings.rsi/";
            var markingStates = species == "Vulpkanin"
                ? new[] { "points_crest-legs", "points_fade-legs", "points_sharp-legs", "points_feet" }
                : species == "Feroxi" ? new[] { "feroxi-leg-stripes-blitz", "feroxi-leg-stripes-tiger" }
                : new[] { "patch", "points" };
            foreach (var markingState in markingStates)
            {
                using var marking = Read(markingPath + markingState + ".png", direction);
                for (var y = 22; y < 32; y++)
                for (var x = 0; x < 32; x++)
                    if (marking[x, y].A >= 128)
                        Assert.That(limbs[x, y].A, Is.GreaterThanOrEqualTo(128),
                            $"{species} marking {markingState} escapes leg at {direction}/{x}/{y}.");
            }
            for (var row = 0; row < outfits.Length + 3; row++)
            {
                for (var side = 0; side < 2; side++)
                    Overlay(preview, body, (direction * 2 + side) * 64 + 16, row * 64 + 16);
                if (row == 0) continue;
                if (row == outfits.Length + 1)
                {
                    using var marking = Read(markingPath + markingStates[0] + ".png", direction);
                    for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        var p = marking[x, y];
                        marking[x, y] = new Rgba32(240, 80, 130, p.A);
                    }
                    Overlay(preview, marking, direction * 128 + 16, row * 64 + 16);
                    Overlay(preview, marking, direction * 128 + 80, row * 64 + 16);
                    continue;
                }
                if (row == outfits.Length + 2)
                {
                    Overlay(preview, shoe, direction * 128 + 16, row * 64 + 16);
                    Overlay(preview, fittedShoe, direction * 128 + 64, row * 64);
                    continue;
                }
                var (path, hem, outer) = outfits[row - 1];
                using var cloth = Read(path, direction);
                var state = outer ? "outerwear" : "jumpsuit" + (female ? "-female" : "");
                using var displacement = Read((outer || hem || species == "Feroxi" ? HemMaps : Maps) + state +
                    (hem ? "-hem" : species == "Feroxi" ? "-feroxi" : "") + ".png", direction);
                using var map = ClothingBodyFitMap.Build(body, [], cloth, displacement, 16, true);
                using var fitted = Render(cloth, map);
                if (!hem)
                {
                    foreach (var part in new[] { "l_leg", "r_leg" })
                    {
                        using var leg = Read($"{folder}/Mobs/Species/{species}/parts.rsi/{part}.png", direction);
                        for (var y = 22; y < 30; y++)
                        for (var x = 0; x < 32; x++)
                            if (leg[x, y].A >= 128)
                                Assert.That(fitted[x + 16, y + 16].A >= 128 || fittedShoe[x + 16, y + 16].A >= 128,
                                    Is.True, $"Closed trousers expose {species} leg at {direction}/{x}/{y}.");
                    }
                }
                for (var y = 22; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    if (hem)
                        Assert.That(fitted[x + 16, y + 16], Is.EqualTo(cloth[x, y]),
                            $"{path}: hem changed at {direction}/{x}/{y}");
                    else if (fitted[x + 16, y + 16] != cloth[x, y]) changed++;
                }
                Overlay(preview, cloth, direction * 128 + 16, row * 64 + 16);
                Overlay(preview, fitted, direction * 128 + 64, row * 64);
                Overlay(preview, shoe, direction * 128 + 16, row * 64 + 16);
                Overlay(preview, fittedShoe, direction * 128 + 64, row * 64);

                // The same displacement must still compose with ordinary torso anatomy fitting.
                using var torso = Read($"{folder}/Mobs/Species/{species}/parts.rsi/torso_{(female ? "f" : "m")}.png", direction);
                using var shape = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_4_0_FRONT.png", direction);
                using var anchor = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_1_0_FRONT.png", direction);
                using var combined = ClothingBodyFitMap.Build(torso, [(shape, anchor)], cloth, displacement, 16, true);
                using var combinedCloth = Render(cloth, combined);
                Assert.That(Opaque(combinedCloth), Is.GreaterThan(0));
            }
        }
        Assert.That(changed, Is.GreaterThan(0), "Trousers must follow the leg displacement.");
        preview.Mutate(p => p.Resize(preview.Width * 3, preview.Height * 3, KnownResamplers.NearestNeighbor));
        preview.SaveAsPng(Path.Combine(TestContext.CurrentContext.TestDirectory, $"digitigrade-{species}-{(female ? "female" : "male")}.png"));
    }

    private static int Opaque(Image<Rgba32> image)
    {
        var count = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if (image[x, y].A >= 128) count++;
        return count;
    }

    private static Image<Rgba32> Read(string path, int direction)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Resources", "Textures"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        using var image = Image.Load<Rgba32>(Path.Combine(root.FullName, "Resources", "Textures", path));
        var columns = image.Width / 32;
        return image.Clone(p => p.Crop(new Rectangle(direction % columns * 32, direction / columns * 32, 32, 32)));
    }

    private static Image<Rgba32> Render(Image<Rgba32> cloth, Image<Rgba32> map)
    {
        var result = new Image<Rgba32>(map.Width, map.Height);
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            var pixel = map[x, y];
            if (pixel.A == 0) continue;
            var source = cloth[pixel.R, pixel.G];
            source.A = (byte) (source.A * pixel.A / 255);
            result[x, y] = source;
        }
        return result;
    }

    private static void Overlay(Image<Rgba32> output, Image<Rgba32> input, int dx, int dy)
    {
        for (var y = 0; y < input.Height; y++)
        for (var x = 0; x < input.Width; x++)
            if (input[x, y].A > 0) output[x + dx, y + dy] = input[x, y];
    }
}
