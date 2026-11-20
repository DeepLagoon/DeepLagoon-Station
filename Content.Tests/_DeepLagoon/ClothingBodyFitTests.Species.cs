#nullable enable
using System;
using System.IO;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests._DeepLagoon;

public sealed partial class ClothingBodyFitTests
{
    [Test]
    public void AnatomyInverseMapKeepsLargeFramesAndTransparentHolesInsideTheirOwnFrame()
    {
        using var source = new Image<Rgba32>(64, 64, new Rgba32(74, 123, 187, 255));
        using var map = new Image<Rgba32>(32, 32, new Rgba32(128, 128, 0, 255));
        map[0, 0] = new Rgba32(255, 128, 0, 255);
        map[31, 31] = new Rgba32(128, 0, 0, 255);
        map[10, 10] = new Rgba32(128, 128, 0, 0);
        using var points = BodyFeatureDisplacementMap.Coordinates(64, 64, map);
        using var cpu = BodyFeatureDisplacementMap.Apply(source, map);
        using var shaderReference = Render(source, points);
        Assert.That(cpu[0, 0].A, Is.Zero, "A source outside this RSI frame must not sample the next atlas state.");
        Assert.That(cpu[63, 63].A, Is.Zero);
        Assert.That(cpu[20, 20].A, Is.Zero, "Authored transparent cut-outs must survive the warp.");
        Assert.That(cpu[32, 32], Is.EqualTo(source[32, 32]));
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.That(cpu[x, y], Is.EqualTo(shaderReference[x, y]), $"CPU and encoded shader sampling disagree at {x},{y}.");
    }

    [TestCase("Human")]
    [TestCase("Vox")]
    [TestCase("Resomi")]
    public void NativeSpeciesUnderwearAndUniformKeepTheirDisplacementBeforeBodyFit(string species)
    {
        var root = species switch {
            "Vox" => "Mobs/Species/Vox",
            "Resomi" => "_Moffstation/Mobs/Species/Resomi",
            _ => "Mobs/Species/Human",
        };
        using var preview = new Image<Rgba32>(512, 192);
        for (var direction = 0; direction < 4; direction++)
        {
            using var body = Read($"{root}/parts.rsi/{(species == "Vox" ? "torso" : "torso_f")}.png", direction);
            foreach (var part in new[] { "l_leg", "r_leg" })
            {
                using var limb = Read($"{root}/parts.rsi/{part}.png", direction); Overlay(body, limb, 0, 0);
            }
            using var skin = body.Clone();
            foreach (var part in new[] { species == "Vox" ? "head" : "head_f", "l_arm", "r_arm", "l_foot", "r_foot" })
            {
                using var limb = Read($"{root}/parts.rsi/{part}.png", direction); Overlay(skin, limb, 0, 0);
            }
            using var hands = Read($"{root}/parts.rsi/l_hand.png", direction);
            using var otherHand = Read($"{root}/parts.rsi/r_hand.png", direction); Overlay(hands, otherHand, 0, 0);
            using var breasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_FRONT.png", direction);
            using var behind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_BEHIND.png", direction); Overlay(breasts, behind, 0, 0);
            using var breastAnchor = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_a_0_FRONT.png", direction);
            using var anchorBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_c_0_BEHIND.png", direction); Overlay(breastAnchor, anchorBehind, 0, 0);
            using var butt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_4_0_FRONT.png", direction);
            using var buttAnchor = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_1_0_FRONT.png", direction);
            using var smallBreasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_i_0_FRONT.png", direction);
            using var smallBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_i_0_BEHIND.png", direction); Overlay(smallBreasts, smallBehind, 0, 0);
            using var smallButt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_3_0_FRONT.png", direction);
            using var uniformDisplacement = Read($"{root}/displacement.rsi/{(species == "Human" ? "jumpsuit-female" : "jumpsuit")}.png", direction);
            using var anatomyDisplacement = species == "Human" ? uniformDisplacement.Clone()
                : Read($"_DeepLagoon/BodyCustomization/species_displacement.rsi/{species.ToLowerInvariant()}.png", direction);
            Image<Rgba32> Native(Image<Rgba32> source) => species == "Human" ? source.Clone()
                : BodyFeatureDisplacementMap.Apply(source, anatomyDisplacement);
            using var nativeBreasts = Native(breasts); using var nativeBreastAnchor = Native(breastAnchor);
            using var nativeButt = Native(butt); using var nativeButtAnchor = Native(buttAnchor);
            using var nativeSmallBreasts = Native(smallBreasts); using var nativeSmallButt = Native(smallButt);
            if (species != "Human")
            {
                static int Area(Image<Rgba32> image)
                {
                    var area = 0;
                    for (var y = 0; y < image.Height; y++)
                    for (var x = 0; x < image.Width; x++) if (image[x, y].A != 0) area++;
                    return area;
                }
                static int Top(Image<Rgba32> image)
                {
                    for (var y = 0; y < image.Height; y++)
                    for (var x = 0; x < image.Width; x++) if (image[x, y].A != 0) return y;
                    return image.Height;
                }
                var shift = species == "Resomi" ? 3 : 1;
                Assert.That(Area(nativeBreasts), Is.EqualTo(Area(breasts)), "A clothing mask must not erase the selected breast volume.");
                Assert.That(Area(nativeBreasts), Is.GreaterThan(Area(nativeBreastAnchor) * 2));
                Assert.That(Area(nativeButt), Is.GreaterThan(Area(nativeButtAnchor) * 1.5), "Large hips must stay visibly larger than their small attachment.");
                Assert.That(Top(nativeBreastAnchor), Is.EqualTo(Top(breastAnchor) + shift), "The attachment must move to the native torso.");
                for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    Assert.That(anatomyDisplacement[x, y].R, Is.EqualTo(128), "Preserve horizontal body volume.");
                    Assert.That(anatomyDisplacement[x, y].A, Is.EqualTo(255), "Anatomy must not inherit garment cut-outs.");
                    Assert.That(y + anatomyDisplacement[x, y].G - 128, Is.LessThanOrEqualTo(31));
                }
                Assert.That(anatomyDisplacement[16, 31].G, Is.EqualTo(128), "The lower frame boundary must not move beyond the floor.");
            }
            if (species != "Human")
                foreach (var source in new[] { breasts, breastAnchor, butt, buttAnchor, smallBreasts, smallButt })
                {
                    using var warped = BodyFeatureDisplacementMap.Apply(source, anatomyDisplacement);
                    using var points = BodyFeatureDisplacementMap.Coordinates(source.Width, source.Height, anatomyDisplacement);
                    using var shaderReference = Render(source, points);
                    for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                        Assert.That(warped[x, y], Is.EqualTo(shaderReference[x, y]), $"{species}/{direction}: native anatomy and inverse-map renderer must agree.");
                }
            var tint = species switch { "Vox" => new Rgba32(108, 116, 29), "Resomi" => new Rgba32(250, 247, 247), _ => new Rgba32(216, 177, 145) };
            for (var scenario = 0; scenario < 3; scenario++)
            {
                for (var mode = 0; mode < 2; mode++)
                {
                    var native = mode == 1;
                    using var visibleBody = skin.Clone();
                    if (scenario == 1) { Overlay(visibleBody, native ? nativeButt : butt, 0, 0); Overlay(visibleBody, native ? nativeBreasts : breasts, 0, 0); }
                    Tint(visibleBody, tint);
                    using var scene = new Image<Rgba32>(64, 64); Overlay(scene, visibleBody, 16, 16);
                    var garments = scenario == 2 ? new[] { "Clothing/Uniforms/Jumpsuit/engineering.rsi/equipped-INNERCLOTHING.png" }
                        : new[] { "_DeepLagoon/Clothing/Under/Top/grey.rsi/equipped-UNDERWEARTOP.png", "_DeepLagoon/Clothing/Under/Bottom/grey.rsi/equipped-UNDERWEARBOTTOM.png" };
                    foreach (var path in garments)
                    {
                        using var cloth = Read(path, direction);
                        var displacement = scenario == 2 || mode == 1 && species != "Human" ? uniformDisplacement : null;
                        var shapeBreasts = scenario == 2 ? native ? nativeSmallBreasts : smallBreasts : native ? nativeBreasts : breasts;
                        var shapeButt = scenario == 2 ? native ? nativeSmallButt : smallButt : native ? nativeButt : butt;
                        var fit = scenario == 2 || scenario == 1 && mode == 1;
                        ClothingBodyFitMap.Anatomy[] anatomy = fit ? [new(shapeButt, native ? nativeButtAnchor : buttAnchor, StretchVertically: false, PartialCoverage: scenario != 2),
                            new(shapeBreasts, native ? nativeBreastAnchor : breastAnchor, PartialCoverage: scenario != 2)] : [];
                        ClothingBodyFitMap.Contour[] contours = !fit ? [] : direction == 0 ? [new(shapeBreasts)]
                            : direction == 1 ? [new(shapeButt, BelowWaist: true)] : [];
                        using var map = ClothingBodyFitMap.Build(body, anatomy, cloth, displacement, 16, true, hands, contours)!;
                        using var rendered = Render(cloth, map);
                        if (scenario == 0 && mode == 1)
                        {
                            // Independent native-shader reference: source texel comes directly
                            // from the authored integer displacement, including transparent holes.
                            for (var y = 0; y < 32; y++)
                            for (var x = 0; x < 32; x++)
                            {
                                var d = displacement?[x, y] ?? new Rgba32(128, 128, 0, 255);
                                var sx = x + d.R - 128; var sy = y + d.G - 128;
                                var expected = sx < 0 || sx >= 32 || sy < 0 || sy >= 32 ? default : cloth[sx, sy];
                                expected.A = (byte) (expected.A * d.A / 255);
                                Assert.That(rendered[x + 16, y + 16].A, Is.EqualTo(expected.A), $"{species}/{direction}/{path}/{x},{y}");
                            }
                        }
                        Overlay(scene, rendered, 0, 0);
                    }
                    using var tintedHands = hands.Clone(); Tint(tintedHands, tint); Overlay(scene, tintedHands, 16, 16);
                    Overlay(preview, scene, direction * 128 + mode * 64, scenario * 64);
                }
            }
        }
        var output = Environment.GetEnvironmentVariable("SPECIES_CLOTHING_PREVIEW_DIRECTORY");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            preview.SaveAsPng(Path.Combine(output, species.ToLowerInvariant() + "-raw.png"));
        }
    }
}
