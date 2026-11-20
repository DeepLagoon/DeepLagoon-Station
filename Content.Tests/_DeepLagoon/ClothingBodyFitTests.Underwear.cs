#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Content.Client._DeepLagoon.BodyCustomization;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests._DeepLagoon;

public sealed partial class ClothingBodyFitTests
{
    [Test]
    public void UnderwearUsesTheSameCompressedSilhouetteAsACoveringUniform()
    {
        using var preview = new Image<Rgba32>(64 * 8, 64);
        var protrusions = new int[2];
        for (var direction = 0; direction < 4; direction++)
        {
            using var body = Read("Mobs/Species/Human/parts.rsi/torso_f.png", direction);
            foreach (var name in new[] { "l_leg", "r_leg" })
            {
                using var limb = Read($"Mobs/Species/Human/parts.rsi/{name}.png", direction);
                Overlay(body, limb, 0, 0);
            }
            using var skin = body.Clone();
            foreach (var name in new[] { "head_f", "l_arm", "r_arm", "l_foot", "r_foot" })
            {
                using var limb = Read($"Mobs/Species/Human/parts.rsi/{name}.png", direction);
                Overlay(skin, limb, 0, 0);
            }
            using var hands = Read("Mobs/Species/Human/parts.rsi/l_hand.png", direction);
            using var rightHand = Read("Mobs/Species/Human/parts.rsi/r_hand.png", direction);
            Overlay(hands, rightHand, 0, 0);
            Tint(skin, new Rgba32(216, 177, 145));
            using var rawButt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_4_0_FRONT.png", direction);
            using var fitButt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_3_0_FRONT.png", direction);
            using var buttAnchor = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_1_0_FRONT.png", direction);
            using var rawBreasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_FRONT.png", direction);
            using var fitBreasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_i_0_FRONT.png", direction);
            using var breastAnchor = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_a_0_FRONT.png", direction);
            using var rawBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_BEHIND.png", direction);
            using var fitBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_i_0_BEHIND.png", direction);
            using var anchorBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_c_0_BEHIND.png", direction);
            Overlay(rawBreasts, rawBehind, 0, 0); Overlay(fitBreasts, fitBehind, 0, 0); Overlay(breastAnchor, anchorBehind, 0, 0);
            using var uniform = Read("Clothing/Uniforms/Jumpsuit/engineering.rsi/equipped-INNERCLOTHING.png", direction);
            using var displacement = Read("Mobs/Species/Human/displacement.rsi/jumpsuit-female.png", direction);
            ClothingBodyFitMap.Anatomy[] Shapes(bool raw, bool partial) => [
                new(raw ? rawButt : fitButt, buttAnchor, StretchVertically: false, PartialCoverage: partial),
                new(raw ? rawBreasts : fitBreasts, breastAnchor, PartialCoverage: partial) ];
            ClothingBodyFitMap.Contour[] Contours(bool raw) => direction == 0 ? [new(raw ? rawBreasts : fitBreasts)]
                : direction == 1 ? [new(raw ? rawButt : fitButt, BelowWaist: true)] : [];
            using var uniformMap = ClothingBodyFitMap.Build(body, Shapes(false, false), uniform, displacement, 16, true, hands, Contours(false))!;
            using var fittedUniform = Render(uniform, uniformMap);
            Tint(hands, new Rgba32(216, 177, 145));
            for (var mode = 0; mode < 2; mode++)
            {
                using var scene = new Image<Rgba32>(64, 64);
                Overlay(scene, skin, 16, 16);
                foreach (var path in new[] { "Top/grey.rsi/equipped-UNDERWEARTOP.png", "Bottom/grey.rsi/equipped-UNDERWEARBOTTOM.png" })
                {
                    using var cloth = Read("_DeepLagoon/Clothing/Under/" + path, direction);
                    using var map = ClothingBodyFitMap.Build(body, Shapes(mode == 0, true), cloth, null, 16, true, hands, Contours(mode == 0))!;
                    using var result = Render(cloth, map);
                    for (var y = 0; y < 64; y++)
                    for (var x = 0; x < 64; x++)
                        if (result[x, y].A >= 128 && fittedUniform[x, y].A < 128) protrusions[mode]++;
                    Overlay(scene, result, 0, 0);
                }
                Overlay(scene, fittedUniform, 0, 0);
                Overlay(scene, hands, 16, 16);
                Overlay(preview, scene, direction * 128 + mode * 64, 0);
            }
        }
        Assert.That(protrusions[1], Is.LessThanOrEqualTo(protrusions[0]), "Covered underwear must not retain the larger bare-body fit.");
        TestContext.Out.WriteLine($"Underwear pixels outside uniform: bare-body fit={protrusions[0]}, matching fit={protrusions[1]}");
        var output = Environment.GetEnvironmentVariable("UNDERWEAR_FIT_PREVIEW");
        if (!string.IsNullOrEmpty(output))
            preview.SaveAsPng(Path.Combine(Path.GetDirectoryName(output)!, "underwear-outfit-raw.png"));
    }

    [Test]
    public void UnderwearFitsLargeVisibleAnatomyAndPreservesSparseFabric()
    {
        // Exactly the runtime mapper and packaged RSI frames; this is an offline
        // sprite comparison, not a screenshot from a running game client.
        var styles = new[] { (Top: "grey", Bottom: "grey"), (Top: "lace", Bottom: "lace"), (Top: "sport", Bottom: "fbriefs") };
        using var preview = new Image<Rgba32>(64 * 8, 64 * styles.Length);
        for (var style = 0; style < styles.Length; style++)
        {
            var topChanged = 0;
            var bottomChanged = 0;
            for (var direction = 0; direction < 4; direction++)
            {
                using var body = Read("Mobs/Species/Human/parts.rsi/torso_f.png", direction);
                foreach (var name in new[] { "l_leg", "r_leg" })
                {
                    using var limb = Read($"Mobs/Species/Human/parts.rsi/{name}.png", direction);
                    Overlay(body, limb, 0, 0);
                }
                using var hands = Read("Mobs/Species/Human/parts.rsi/l_hand.png", direction);
                using var rightHand = Read("Mobs/Species/Human/parts.rsi/r_hand.png", direction);
                Overlay(hands, rightHand, 0, 0);
                using var butt = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_4_0_FRONT.png", direction);
                using var buttAnchor = Read("_DeepLagoon/BodyCustomization/butt.rsi/butt_pair_1_0_FRONT.png", direction);
                using var breasts = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_FRONT.png", direction);
                using var breastAnchor = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_a_0_FRONT.png", direction);
                using var behind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_j_0_BEHIND.png", direction);
                using var anchorBehind = Read("_DeepLagoon/BodyCustomization/breasts.rsi/breasts_pair_c_0_BEHIND.png", direction);
                Overlay(breasts, behind, 0, 0);
                Overlay(breastAnchor, anchorBehind, 0, 0);
                var anatomy = new[] {
                    new ClothingBodyFitMap.Anatomy(butt, buttAnchor, StretchVertically: false, PartialCoverage: true),
                    new ClothingBodyFitMap.Anatomy(breasts, breastAnchor, PartialCoverage: true),
                };
                var contours = new List<ClothingBodyFitMap.Contour>();
                if (direction == 0) contours.Add(new(breasts));
                if (direction == 1) contours.Add(new(butt, BelowWaist: true));
                using var skin = body.Clone();
                foreach (var name in new[] { "head_f", "l_arm", "r_arm", "l_foot", "r_foot" })
                {
                    using var limb = Read($"Mobs/Species/Human/parts.rsi/{name}.png", direction);
                    Overlay(skin, limb, 0, 0);
                }
                Overlay(skin, butt, 0, 0); Overlay(skin, breasts, 0, 0);
                Tint(skin, new Rgba32(216, 177, 145));
                using var original = new Image<Rgba32>(64, 64);
                using var fitted = new Image<Rgba32>(64, 64);
                Overlay(original, skin, 16, 16); Overlay(fitted, skin, 16, 16);
                foreach (var upper in new[] { false, true })
                {
                    var path = upper ? $"Top/{styles[style].Top}.rsi/equipped-UNDERWEARTOP.png"
                        : $"Bottom/{styles[style].Bottom}.rsi/equipped-UNDERWEARBOTTOM.png";
                    using var cloth = Read("_DeepLagoon/Clothing/Under/" + path, direction);
                    using var map = ClothingBodyFitMap.Build(body, anatomy, cloth, null, 16, true, hands, contours)!;
                    using var result = Render(cloth, map);
                    var changed = 0;
                    for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        if (result[x + 16, y + 16] != cloth[x, y]) changed++;
                        var sample = map[x + 16, y + 16];
                        if (sample.A > 0 && cloth[sample.R, sample.G].A == 0)
                            Assert.That(result[x + 16, y + 16].A, Is.Zero, "Fitting must preserve garment cut-outs.");
                    }
                    if (upper) topChanged += changed;
                    else bottomChanged += changed;
                    Overlay(original, cloth, 16, 16); Overlay(fitted, result, 0, 0);
                }
                Tint(hands, new Rgba32(216, 177, 145));
                Overlay(original, hands, 16, 16); Overlay(fitted, hands, 16, 16);
                Overlay(preview, original, direction * 128, style * 64);
                Overlay(preview, fitted, direction * 128 + 64, style * 64);
            }
            Assert.That(topChanged, Is.GreaterThan(0), styles[style].Top + " bra must follow the selected breasts.");
            Assert.That(bottomChanged, Is.GreaterThan(0), styles[style].Bottom + " briefs must follow the selected hips.");
        }
        var output = Environment.GetEnvironmentVariable("UNDERWEAR_FIT_PREVIEW");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            preview.SaveAsPng(output);
        }
    }
}
