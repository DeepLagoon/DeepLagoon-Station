using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BlueMoonCustomizationTests
{
    [Test]
    public async Task ImportedWingAndTailLayersStraddleTheHumanBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                foreach (var category in new[] { MarkingCategories.Wings, MarkingCategories.Tail })
                {
                    var marking = prototypes.EnumeratePrototypes<MarkingPrototype>().First(p => p.ID.StartsWith("BlueMoon")
                        && p.MarkingCategory == category && p.SpeciesRestrictions == null && p.SexRestriction == null
                        && p.SpriteLayersBefore.Values.Contains(HumanoidVisualLayers.Chest));
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                    profile = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new() { marking.AsMarking() }));
                    entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>().LoadProfile(dummy, profile);
                    var sprite = entities.GetComponent<SpriteComponent>(dummy);
                    foreach (var (state, anchor) in marking.SpriteLayersBefore)
                    {
                        var index = sprite.LayerMapGet(marking.ID + "-" + state);
                        Assert.That(sprite[index].Visible, Is.True, marking.ID);
                        Assert.That(index, Is.LessThan(sprite.LayerMapGet(anchor)), marking.ID);
                    }
                    foreach (var (state, anchor) in marking.SpriteLayers)
                        Assert.That(sprite.LayerMapGet(marking.ID + "-" + state), Is.GreaterThan(sprite.LayerMapGet(anchor)), marking.ID);
                }
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ImportedMarkingsLoadEverySpriteState()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var resources = pair.Client.ResolveDependency<IResourceCache>();
            var markings = prototypes.EnumeratePrototypes<MarkingPrototype>().Where(p => p.ID.StartsWith("BlueMoon")).ToArray();
            Assert.That(markings, Is.Not.Empty);
            Assert.That(markings.Any(p => p.MarkingCategory == MarkingCategories.Hair), Is.True);
            Assert.That(markings.Any(p => p.MarkingCategory == MarkingCategories.FacialHair), Is.True);
            foreach (var marking in markings)
            foreach (var sprite in marking.Sprites.Cast<SpriteSpecifier.Rsi>())
            {
                var rsi = resources.GetResource<RSIResource>(new ResPath("/Textures") / sprite.RsiPath).RSI;
                Assert.That(rsi.TryGetState(sprite.RsiState, out _), Is.True, marking.ID + "/" + sprite.RsiState);
            }
        });
        await pair.CleanReturnAsync();
    }
}
