#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class ClothingBodyFitTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void VulpkaninMercenaryMedicFitsCombinedAnatomyWithoutMovingSleeves(bool female)
    {
        using var preview = new Image<Rgba32>(64 * 8, 64);
        using var contourPreview = new Image<Rgba32>(64 * 8, 64);
        var failures = new List<string>();
        for (var direction = 0; direction < 4; direction++)
        {
            using var body = Read($"_DV/Mobs/Species/Vulpkanin/parts.rsi/torso_{(female ? "f" : "m")}.png", direction);
            using var leftLeg = Read("_DV/Mobs/Species/Vulpkanin/parts.rsi/l_leg.png", direction);
            using var rightLeg = Read("_DV/Mobs/Species/Vulpkanin/parts.rsi/r_leg.png", direction);
            Overlay(body, leftLeg, 0, 0); Overlay(body, rightLeg, 0, 0);
            using var hands = Read("_DV/Mobs/Species/Vulpkanin/parts.rsi/l_hand.png", direction);
            using var rightHand = Read("_DV/Mobs/Species/Vulpkanin/parts.rsi/r_hand.png", direction);
            Overlay(hands, rightHand, 0, 0);
            using var butt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_4_0_FRONT.png", direction);
            using var buttAnchor = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_1_0_FRONT.png", direction);
            using var breasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_FRONT.png", direction);
            using var breastsBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_BEHIND.png", direction);
            using var breastAnchor = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_a_0_FRONT.png", direction);
            using var breastAnchorBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_c_0_BEHIND.png", direction);
            Overlay(breasts, breastsBehind, 0, 0); Overlay(breastAnchor, breastAnchorBehind, 0, 0);
            using var original = new Image<Rgba32>(64, 64);
            using var fitted = new Image<Rgba32>(64, 64);
            using var flat = new Image<Rgba32>(64, 64);
            var layers = new[] {
                ("base-bottom", new Rgba32(65, 47, 36)),
                ("base-top-turtleneck-alt", new Rgba32(73, 110, 160)),
                ("belt", new Rgba32(62, 62, 72)),
                ("belt-buckle", new Rgba32(255, 255, 255)),
                ("decor-shoulder-patch", new Rgba32(174, 183, 189)),
            };
            using var coverage = new Image<Rgba32>(32, 32);
            foreach (var (name, _) in layers)
            {
                using var cloth = Read($"_NF/Clothing/Uniforms/Jumpsuit/military.rsi/equipped-{name}.png", direction);
                Overlay(coverage, cloth, 0, 0);
            }
            Image<Rgba32>? contourShape = direction == 0 ? breasts : direction == 1 ? butt : null;
            using var flatMap = ClothingBodyFitMap.Build(body, [(butt, buttAnchor), (breasts, breastAnchor)], coverage, null, 16, true, hands) ?? throw new AssertionException("Expected a body fit map for this fixture");
            using var map = ClothingBodyFitMap.Build(body, [(butt, buttAnchor), (breasts, breastAnchor)], coverage, null, 16, true, hands,
                contourShape == null ? null : [new(contourShape, BelowWaist: direction == 1)]) ?? throw new AssertionException("Expected a body fit map for this fixture");
            var shaded = 0;
            var bent = 0;
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var before = flatMap[x + 16, y + 16];
                var after = map[x + 16, y + 16];
                if (contourShape == null || contourShape[x, y].A < 128 || hands[x, y].A >= 128)
                    Assert.That(after, Is.EqualTo(before), $"Contour escaped its surface: direction={direction}, ({x},{y}).");
                if (direction == 1 && y < 21)
                    Assert.That(after, Is.EqualTo(before), $"Rear volume changed the back above the waist at ({x},{y}).");
                if (after.B != before.B) shaded++;
                if (after.R != before.R || after.G != before.G) bent++;
            }
            if (contourShape != null)
            {
                Assert.That(shaded, Is.GreaterThan(0), $"Missing volume shading in direction {direction}.");
                Assert.That(bent, Is.GreaterThan(0), $"Missing fabric bend in direction {direction}.");
            }
            foreach (var (name, tint) in layers)
            {
                using var cloth = Read($"_NF/Clothing/Uniforms/Jumpsuit/military.rsi/equipped-{name}.png", direction);
                using var result = Render(cloth, map);
                using var flatResult = Render(cloth, flatMap);
                Tint(cloth, tint); Tint(result, tint); Tint(flatResult, tint);
                Overlay(original, cloth, 16, 16); Overlay(fitted, result, 0, 0);
                Overlay(flat, flatResult, 0, 0);
                // These light pixels are garment decorations, not exposed skin. Keep the asymmetric
                // shoulder patch and the front-facing metal belt buckle while repairing actual holes.
                if (name == "decor-shoulder-patch" && direction is 0 or 1)
                    Assert.That(result[(direction == 0 ? 9 : 21) + 16, 12 + 16].A, Is.EqualTo(255));
                if (name == "belt-buckle" && direction is 2 or 3)
                    Assert.That(result[(direction == 2 ? 18 : 13) + 16, 20 + 16].A, Is.EqualTo(255));
                if (direction == 0 && name == "base-top-turtleneck-alt")
                {
                    for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        // Keep the outer edge at the larger of the existing sleeve and the new torso.
                        if (result[x + 16, y + 16].A < 128) continue;
                        var left = 32;
                        var right = -1;
                        for (var sx = 0; sx < 32; sx++)
                            if (cloth[sx, y].A >= 128 || breasts[sx, y].A >= 128 || butt[sx, y].A >= 128 || body[sx, y].A >= 128)
                            { left = Math.Min(left, sx); right = Math.Max(right, sx); }
                        if (x < left - 1 || x > right + 1) failures.Add($"Oversized sleeve at ({x},{y}).");
                    }
                }
            }
            using var silhouette = body.Clone();
            foreach (var limb in new[] { "l_arm", "r_arm", "l_hand", "r_hand" })
            {
                using var image = Read($"_DV/Mobs/Species/Vulpkanin/parts.rsi/{limb}.png", direction);
                Overlay(silhouette, image, 0, 0);
            }
            Overlay(silhouette, butt, 0, 0); Overlay(silhouette, breasts, 0, 0);
            using var gloves = Read("Clothing/Hands/Gloves/combat.rsi/equipped-HAND.png", direction);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                Assert.That(fitted[x + 16, y + 16].A, Is.EqualTo(flat[x + 16, y + 16].A),
                    $"Contour changed garment coverage: direction={direction}, ({x},{y}).");
                if (butt[x, y].A >= 128 && body[x, y].A < 128 && fitted[x + 16, y + 16].A < 128)
                    failures.Add($"Uncovered butt in direction {direction} at ({x},{y}).");
                if ((butt[x, y].A >= 128 || breasts[x, y].A >= 128) &&
                    fitted[x + 16, y + 16].A < 128 && gloves[x, y].A < 128)
                {
                    var sample = map[x + 16, y + 16];
                    failures.Add($"Skin outside gloves in direction {direction} at ({x},{y}), source ({sample.R},{sample.G}).");
                }
                if (silhouette[x, y].A >= 128 && original[x + 16, y + 16].A >= 128 &&
                    fitted[x + 16, y + 16].A < 128 && gloves[x, y].A < 128)
                    failures.Add($"Previously covered skin in direction {direction} at ({x},{y}).");
            }
            Tint(silhouette, new Rgba32(198, 166, 127));
            for (var side = 0; side < 2; side++)
            {
                var x = (direction * 2 + side) * 64;
                Overlay(preview, silhouette, x + 16, 16);
                Overlay(preview, side == 0 ? original : fitted, x, 0);
                Overlay(preview, gloves, x + 16, 16);
                Overlay(contourPreview, silhouette, x + 16, 16);
                Overlay(contourPreview, side == 0 ? flat : fitted, x, 0);
                Overlay(contourPreview, gloves, x + 16, 16);
            }
        }
        preview.Mutate(p => p.Resize(preview.Width * 4, preview.Height * 4, KnownResamplers.NearestNeighbor));
        preview.SaveAsPng(Path.Combine(TestContext.CurrentContext.TestDirectory, $"clothing-fit-vulpkanin-{(female ? "female" : "male")}.png"));
        contourPreview.Mutate(p => p.Resize(contourPreview.Width * 4, contourPreview.Height * 4, KnownResamplers.NearestNeighbor));
        contourPreview.SaveAsPng(Path.Combine(TestContext.CurrentContext.TestDirectory, $"clothing-fit-contours-{(female ? "female" : "male")}.png"));
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }

    [Test]
    public void StretchPreservesTextureAndCutoutsWithoutLeakingAtlasPixels()
    {
        using var body = Box(16, 16, 5, 3, 11, 13);
        using var shape = Box(16, 16, 1, 5, 15, 11);
        using var anchor = Box(16, 16, 6, 6, 10, 10);
        using var cloth = body.Clone();
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            if (cloth[x, y].A > 0) cloth[x, y] = new Rgba32((byte) (x * 13), (byte) (y * 13), 72, 255);
        cloth[8, 8] = default;
        using var map = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, null, 8) ?? throw new AssertionException("Expected a body fit map for this fixture");
        Assert.That(map, Is.Not.Null);
        using var result = Render(cloth, map);
        Assert.That(result[9, 15].A, Is.EqualTo(255), "Cloth must reach the expanded body's left edge.");
        Assert.That(result[22, 15].A, Is.EqualTo(255), "Cloth must reach the expanded body's right edge.");
        Assert.That(result[16, 16].A, Is.Zero, "An existing fabric cut-out must remain transparent.");
        Assert.That(result[0, 0].A, Is.Zero, "Padding must not sample a neighbouring RSI frame.");
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var p = result[x, y];
            if (p.A == 0) continue;
            Assert.That(p.B, Is.EqualTo(72));
            Assert.That(p.R % 13, Is.Zero);
            Assert.That(p.G % 13, Is.Zero);
        }
    }

    [Test]
    public void ContourWorksInsideBodyAndIgnoresSkinColors()
    {
        using var body = Box(16, 16, 2, 2, 14, 14);
        using var shape = Box(16, 16, 4, 4, 12, 12);
        using var anchor = shape.Clone();
        using var cloth = body.Clone();
        cloth[7, 7] = default; // A designed opening inside the contoured region.
        using var plain = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, null, 8, true) ?? throw new AssertionException("Expected a body fit map for this fixture");
        using var map = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, null, 8, contours: [new(shape)]) ?? throw new AssertionException("Expected a body fit map for this fixture");
        Assert.That(map, Is.Not.Null, "A part need not protrude beyond the body to shape fabric.");
        Tint(shape, new Rgba32(255, 0, 255));
        using var recolored = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, null, 8, contours: [new(shape)]) ?? throw new AssertionException("Expected a body fit map for this fixture");
        using var before = Render(cloth, plain);
        using var after = Render(cloth, map);
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            Assert.That(map[x, y], Is.EqualTo(recolored[x, y]), "Only the silhouette may affect clothed volume.");
            Assert.That(after[x, y].A, Is.EqualTo(before[x, y].A), "Volume must preserve fabric cut-outs.");
            if (after[x, y] != before[x, y]) changed++;
        }
        Assert.That(changed, Is.GreaterThan(0));
    }

    [Test]
    public void FixedHandOpeningsDoNotEraseDesignedCutouts()
    {
        using var body = Box(16, 16, 4, 2, 12, 14);
        using var shape = Box(16, 16, 4, 4, 12, 13);
        using var anchor = Box(16, 16, 5, 4, 11, 7);
        using var hands = Box(16, 16, 6, 5, 7, 6);
        using var cloth = body.Clone();
        cloth[6, 5] = default; // Opening hidden by the stationary hand.
        cloth[9, 5] = default; // Deliberate cut-out over the torso.
        cloth[6, 8] = new Rgba32(45, 76, 123, 255);
        using var map = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, null, 8, hands: hands) ?? throw new AssertionException("Expected a body fit map for this fixture");
        using var result = Render(cloth, map);
        Assert.That(result[6 + 8, 8 + 8], Is.EqualTo(cloth[6, 8]), "The hand opening must not expose skin below the hand.");
        Assert.That(result[9 + 8, 8 + 8].A, Is.Zero, "A deliberate garment cut-out must still follow the fabric.");
    }

    [Test]
    public void UncoveredAttachmentDoesNotStretchUnrelatedClothing()
    {
        using var body = Box(16, 16, 5, 3, 11, 13);
        using var shape = Box(16, 16, 1, 3, 15, 14);
        using var anchor = Box(16, 16, 5, 3, 11, 6);
        using var pants = Box(16, 16, 5, 9, 11, 14);
        using var map = ClothingBodyFitMap.Build(body, [(shape, anchor)], pants, null, 8, contours: [new(shape)]);
        Assert.That(map, Is.Null);
    }

    [Test]
    public void IdentityDirectionKeepsNarrowClothingAndBareSkinUnchanged()
    {
        using var body = Box(16, 16, 4, 3, 12, 13);
        using var cloth = Box(16, 16, 6, 3, 10, 13);
        using var map = ClothingBodyFitMap.Build(body, [], cloth, null, 8, true) ?? throw new AssertionException("Expected a body fit map for this fixture");
        using var result = Render(cloth, map);
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.That(result[x + 8, y + 8], Is.EqualTo(cloth[x, y]));
    }

    [Test]
    public void SpeciesDisplacementIsComposedBeforeClothFit()
    {
        using var body = Box(16, 16, 5, 3, 11, 13);
        using var cloth = Box(16, 16, 4, 3, 10, 13);
        using var displacement = new Image<Rgba32>(16, 16, new Rgba32(127, 128, 0, 255));
        using var map = ClothingBodyFitMap.Build(body, [], cloth, displacement, 8, true) ?? throw new AssertionException("Expected a body fit map for this fixture");
        Assert.That(map[14, 14].R, Is.EqualTo(5));
        Assert.That(map[14, 14].G, Is.EqualTo(6));
        Assert.That(map[8, 8].A, Is.Zero, "A negative displaced source coordinate must not wrap into the atlas.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealUniformFitsLargeAnatomyInAllDirections(bool female)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Textures"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null);
        var root = Path.Combine(directory!.FullName, "Resources", "Textures");
        using var preview = new Image<Rgba32>(64 * 8, 64 * 3);
        var parts = new[] { ("belly", "1", "10"), ("butt", "1", "8"), ("breasts", "a", "impossible") };
        for (var part = 0; part < parts.Length; part++)
        for (var direction = 0; direction < 4; direction++)
        {
            using var body = Read($"Mobs/Species/Human/parts.rsi/torso_{(female ? "f" : "m")}.png", direction);
            using var leftLeg = Read("Mobs/Species/Human/parts.rsi/l_leg.png", direction);
            using var rightLeg = Read("Mobs/Species/Human/parts.rsi/r_leg.png", direction);
            Overlay(body, leftLeg, 0, 0); Overlay(body, rightLeg, 0, 0);
            var (name, small, large) = parts[part];
            using var shape = Read($"_DeepLagoon/BodyCustomization/{name}.rsi/{name}_pair_{large}_0_FRONT.png", direction);
            using var anchor = Read($"_DeepLagoon/BodyCustomization/{name}.rsi/{name}_pair_{small}_0_FRONT.png", direction);
            using var cloth = Read("Clothing/Uniforms/Jumpsuit/engineering.rsi/equipped-INNERCLOTHING.png", direction);
            using var displacement = female ? Read("Mobs/Species/Human/displacement.rsi/jumpsuit-female.png", direction) : null;
            using var identity = ClothingBodyFitMap.Build(body, [], cloth, displacement, 16, true) ?? throw new AssertionException("Expected a body fit map for this fixture");
            using var original = Render(cloth, identity);
            using var map = ClothingBodyFitMap.Build(body, [(shape, anchor)], cloth, displacement, 16, true) ?? throw new AssertionException("Expected a body fit map for this fixture");
            using var fitted = Render(cloth, map);
            for (var side = 0; side < 2; side++)
            {
                var x = (direction * 2 + side) * 64;
                var y = part * 64;
                Overlay(preview, body, x + 16, y + 16);
                Overlay(preview, shape, x + 16, y + 16);
                Overlay(preview, side == 0 ? original : fitted, x, y);
            }
            var before = Exposed(original, shape);
            var after = Exposed(fitted, shape);
            TestContext.Out.WriteLine($"{name} direction={direction}: protruding pixels {before} -> {after}");
            Assert.That(after, Is.Zero, $"{name}, direction {direction}: no skin should protrude through a closed uniform");
        }
        preview.Mutate(p => p.Resize(preview.Width * 3, preview.Height * 3, KnownResamplers.NearestNeighbor));
        preview.SaveAsPng(Path.Combine(TestContext.CurrentContext.TestDirectory, $"clothing-fit-preview-{(female ? "female" : "male")}.png"));

        Image<Rgba32> Read(string path, int direction)
        {
            using var image = Image.Load<Rgba32>(Path.Combine(root, path));
            var columns = image.Width / 32;
            return image.Clone(p => p.Crop(new Rectangle(direction % columns * 32, direction / columns * 32, 32, 32)));
        }
    }

    private static int Exposed(Image<Rgba32> clothing, Image<Rgba32> shape)
    {
        var count = 0;
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            if (shape[x, y].A >= 128 && clothing[x + 16, y + 16].A < 128) count++;
        return count;
    }

    private static Image<Rgba32> Read(string path, int direction)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Textures"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null);
        using var image = Image.Load<Rgba32>(Path.Combine(directory.FullName, "Resources", "Textures", path));
        var columns = image.Width / 32;
        return image.Clone(p => p.Crop(new Rectangle(direction % columns * 32, direction / columns * 32, 32, 32)));
    }

    private static void Tint(Image<Rgba32> image, Rgba32 tint)
    {
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var p = image[x, y];
            image[x, y] = new Rgba32((byte) (p.R * tint.R / 255), (byte) (p.G * tint.G / 255), (byte) (p.B * tint.B / 255), p.A);
        }
    }

    private static Image<Rgba32> Box(int width, int height, int left, int top, int right, int bottom)
    {
        var image = new Image<Rgba32>(width, height);
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++) image[x, y] = new Rgba32(255, 255, 255, 255);
        return image;
    }

    private static Image<Rgba32> Render(Image<Rgba32> cloth, Image<Rgba32> map)
    {
        var result = new Image<Rgba32>(map.Width, map.Height);
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            var p = map[x, y];
            if (p.A == 0) continue;
            var pixel = cloth[p.R, p.G];
            pixel.R = (byte) Math.Clamp((int) MathF.Round(pixel.R * p.B / 128f), 0, 255);
            pixel.G = (byte) Math.Clamp((int) MathF.Round(pixel.G * p.B / 128f), 0, 255);
            pixel.B = (byte) Math.Clamp((int) MathF.Round(pixel.B * p.B / 128f), 0, 255);
            pixel.A = (byte) (pixel.A * p.A / 255);
            result[x, y] = pixel;
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
