using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Client._DeepLagoon.Tattoos;
using Content.Client.Humanoid;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ClothingHairOcclusionIntegrationTests
{
    [TestPrototypes]
    private const string OptOutHat = """
        - type: entity
          id: HairOcclusionOptOutHat
          parent: ClothingHeadHatFedoraBrown
          components:
          - type: Clothing
            hairOcclusion: false
        """;

    [Test]
    public async Task RealHeadwearClipsHairInFourDirectionsAndRestoresItOnRemoval()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
            var appearance = entities.System<HumanoidAppearanceSystem>();
            var inventory = entities.System<InventorySystem>();
            var fit = entities.System<ClothingBodyFitSystem>();
            try
            {
                foreach (var (style, state) in new[]
                {
                    ("HumanHairBusiness4", "business4"), ("HumanHairJoestar", "joestar"), ("HumanHairLong", "long"),
                })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                    profile = profile.WithCharacterAppearance(profile.Appearance.WithHairStyleName(style));
                    profile.Appearance.BodyFeatures.Clear();
                    profile.Appearance.Markings.Clear();
                    appearance.LoadProfile(uid, profile);
                    var hairKey = style + "-" + state;
                    var helper = hairKey + "-body-fit-source";
                    foreach (var prototype in new[] { "ClothingHeadBandBlack", "ClothingHeadHatGreyFlatcap", "ClothingHeadHatFedoraBrown", "ClothingHeadHelmetBasic" })
                    {
                        var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                        try
                        {
                            Assert.That(inventory.TryEquip(uid, item, "head", force: true), Is.True);
                            Assert.That(sprite.LayerMapTryGet(helper, out _), Is.True, prototype + "/" + style);
                            var hair = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(hairKey)];
                            var hat = (SpriteComponent.Layer) sprite[sprite.LayerMapGet("head-0")];
                            Assert.That(hair.ShaderPrototype, Is.EqualTo("DeepLagoonClothingBodyFit"));
                            var count = sprite.AllLayers.Count();
                            for (var i = 0; i < 3; i++) fit.Refresh(uid, humanoid, sprite);
                            Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count), "Repeated refresh must not accumulate helpers.");
                            var hidden = 0;
                            var below = 0;
                            for (var direction = 0; direction < 4; direction++)
                            {
                                using var coverage = TattooSpriteFrames.Read(hat.ActualState!, direction);
                                using var source = TattooSpriteFrames.Read(hair.ActualState!, direction);
                                using var map = ClothingHairOcclusionMap.Build(coverage, source.Width, source.Height);
                                var lowest = -1;
                                for (var y = 0; y < coverage.Height; y++)
                                for (var x = 0; x < coverage.Width; x++)
                                    if (coverage[x, y].A >= 128) lowest = y;
                                for (var y = 0; y < source.Height; y++)
                                for (var x = 0; x < source.Width; x++)
                                {
                                    if (source[x, y].A == 0) continue;
                                    if (map[x, y].A == 0) hidden++;
                                    if (y > lowest)
                                    {
                                        below++;
                                        Assert.That(map[x, y].A, Is.EqualTo(255), "Keep every hair pixel below the hat.");
                                    }
                                }
                            }
                            Assert.That(hidden, Is.GreaterThan(0), "The actual hairstyle must be clipped.");
                            if (style == "HumanHairLong") Assert.That(below, Is.GreaterThan(0), "Long hair must remain visible.");
                            appearance.LoadProfile(uid, profile);
                            Assert.That(sprite.LayerMapTryGet(helper, out _), Is.True, "Rebuilding markings must restore clipping.");
                        }
                        finally
                        {
                            inventory.TryUnequip(uid, "head", force: true, reparent: false);
                            Assert.That(sprite.LayerMapTryGet(helper, out _), Is.False);
                            Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet(hairKey)]).ShaderPrototype, Is.Null);
                            entities.DeleteEntity(item);
                        }
                    }
                }

                foreach (var (prototype, slot) in new[]
                {
                    ("HairOcclusionOptOutHat", "head"), ("ClothingHeadHatHairflower", "head"),
                    ("ClothingHeadHatFlowerWreath", "head"), ("FoodPoppy", "head"), ("FoodLily", "head"),
                    ("FoodPoppyWhite", "head"), ("ClothingHeadHatCatEars", "head"), ("ClothingHeadHatBow", "head"),
                    ("ClothingHeadBandBlack", "pocket1"),
                })
                {
                    var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                    try
                    {
                        Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True);
                        for (var i = 0; i < 3; i++) fit.Refresh(uid, humanoid, sprite);
                        Assert.That(sprite.LayerMapTryGet("HumanHairLong-long-body-fit-source", out _), Is.False, prototype);
                        var hair = (SpriteComponent.Layer) sprite[sprite.LayerMapGet("HumanHairLong-long")];
                        Assert.That(hair.Visible, Is.True, prototype);
                        Assert.That(hair.ShaderPrototype, Is.Null, prototype + " must preserve the original hair.");
                    }
                    finally { inventory.TryUnequip(uid, slot, force: true, reparent: false); entities.DeleteEntity(item); }
                }

                var glowHat = entities.SpawnEntity("ClothingHeadHatFedoraBrown", MapCoordinates.Nullspace);
                try
                {
                    sprite.LayerSetShader("HumanHairLong-long", "unshaded");
                    Assert.That(inventory.TryEquip(uid, glowHat, "head", force: true), Is.True);
                    Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet("HumanHairLong-long")]).ShaderPrototype,
                        Is.EqualTo("DeepLagoonHairOcclusionUnshaded"));
                    inventory.TryUnequip(uid, "head", force: true, reparent: false);
                    Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet("HumanHairLong-long")]).ShaderPrototype, Is.EqualTo("unshaded"));
                }
                finally { entities.DeleteEntity(glowHat); }
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }
}
