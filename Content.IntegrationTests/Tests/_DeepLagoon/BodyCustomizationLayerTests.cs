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
