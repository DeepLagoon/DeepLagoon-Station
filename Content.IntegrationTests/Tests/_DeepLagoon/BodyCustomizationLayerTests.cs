using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Client.Humanoid;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BodyCustomizationLayerTests
{
    [TestCase("Human")]
    [TestCase("Vulpkanin")]
    [TestCase("Synth")]
    public async Task FrontGenitalsDrawAboveTailsAfterAppearanceRefresh(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var visualizer = entities.System<BodyCustomizationVisualizerSystem>();
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
                var sprite = entities.GetComponent<SpriteComponent>(dummy);
                var tails = prototypes.EnumeratePrototypes<MarkingPrototype>()
                    .Where(p => p.BodyPart is HumanoidVisualLayers.Tail or HumanoidVisualLayers.TailExtras).ToArray();
                Assert.That(tails, Is.Not.Empty);
                foreach (var tail in tails)
                foreach (var visibility in new[] { 1, 2 })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(species)
                        .WithTraitPreference("GeneticModification", prototypes);
                    profile.Appearance.Markings.Add(tail.AsMarking());
                    foreach (var part in new[] { "vagina", "anus", "testicles", "penis" })
                    {
                        var feature = BodyCustomizationCatalog.Default(part);
                        feature.Enabled = true;
                        feature.Visibility = visibility;
                        profile.Appearance.BodyFeatures.Add(feature);
                    }
                    appearance.LoadProfile(dummy, profile);
                    var count = sprite.AllLayers.Count();
                    foreach (var aroused in new[] { false, true, false })
                    {
                        // Hidden tails may become visible again without rebuilding their layers.
                        foreach (var rsi in tail.Sprites.OfType<SpriteSpecifier.Rsi>())
                            sprite.LayerSetVisible($"{tail.ID}-{rsi.RsiState}", aroused);
                        humanoid.GenitalAroused = aroused;
                        visualizer.Refresh(dummy, humanoid, sprite);
                        Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                        foreach (var part in new[] { "vagina", "testicles", "penis" })
                            if (sprite.LayerMapTryGet($"dl-body/{part}/BEHIND", out var rear))
                                Assert.That(rear, Is.LessThan(sprite.LayerMapGet(HumanoidVisualLayers.Chest)));
                        foreach (var rsi in tail.Sprites.OfType<SpriteSpecifier.Rsi>())
                        {
                            if (tail.SpriteLayersBefore.ContainsKey(rsi.RsiState)) continue;
                            var index = sprite.LayerMapGet($"{tail.ID}-{rsi.RsiState}");
                            foreach (var part in new[] { "vagina", "testicles", "penis" })
                                Assert.That(sprite.LayerMapGet($"dl-body/{part}/FRONT"), Is.GreaterThan(index),
                                    $"{species}/{tail.ID}/{rsi.RsiState}/{visibility}/{aroused}/{part}");
                        }
                    }
                }
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Human")]
    [TestCase("Synth")]
    public async Task GenitalsDrawAboveArmsAndHandsAfterAppearanceRefresh(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
                var sprite = entities.GetComponent<SpriteComponent>(dummy);
                var visualizer = entities.System<BodyCustomizationVisualizerSystem>();
                foreach (var visibility in new[] { 1, 2, 1 })
                foreach (var aroused in new[] { false, true })
                {
                    entities.System<HumanoidAppearanceSystem>().LoadProfile(dummy, profile);
                    humanoid.BodyFeatures = BodyCustomizationCatalog.Order.Where(part => part != "womb")
                        .Select(part => { var feature = BodyCustomizationCatalog.Default(part);
                            feature.Enabled = true; feature.Visibility = visibility; return feature; }).ToList();
                    humanoid.GenitalAroused = aroused;
                    visualizer.Refresh(dummy, humanoid, sprite);
                    var count = sprite.AllLayers.Count();
                    visualizer.Refresh(dummy, humanoid, sprite);
                    Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                    var limbs = new[] { HumanoidVisualLayers.LArm, HumanoidVisualLayers.RArm,
                        HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand };
                    var keys = limbs.Cast<object>().Append("gloves").ToList();
                    foreach (var marking in humanoid.MarkingSet.Markings.Values.SelectMany(list => list))
                    {
                        var prototype = prototypes.Index<MarkingPrototype>(marking.MarkingId);
                        if (!limbs.Contains(prototype.BodyPart)) continue;
                        foreach (var rsi in prototype.Sprites.OfType<SpriteSpecifier.Rsi>())
                            keys.Add($"{prototype.ID}-{rsi.RsiState}");
                    }
                    var previous = -1;
                    foreach (var part in new[] { "vagina", "anus", "testicles", "penis" })
                    {
                        var index = sprite.LayerMapGet($"dl-body/{part}/FRONT");
                        Assert.That(sprite[index].Visible, Is.True);
                        Assert.That(index, Is.GreaterThan(previous), part);
                        previous = index;
                        foreach (var key in keys)
                            if (sprite.LayerMapTryGet(key, out var limb))
                                Assert.That(index, Is.GreaterThan(limb), $"{species}/{visibility}/{part}/{key}");
                    }
                    humanoid.BodyFeatures.Clear();
                    visualizer.Refresh(dummy, humanoid, sprite);
                    Assert.That(sprite.LayerMapTryGet("dl-body/penis/FRONT", out _), Is.False);
                    Assert.That(sprite.LayerMapGet(HumanoidVisualLayers.LHand), Is.LessThan(sprite.LayerMapGet("gloves")));
                    Assert.That(sprite.LayerMapGet("gloves"), Is.LessThan(sprite.LayerMapGet("shoes")));
                }
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }
}
