using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Client.Humanoid;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ClothingBodyFitIntegrationTests
{
    [TestCase("Vulpkanin")]
    [TestCase("Feroxi")]
    [TestCase("Tajaran")]
    public async Task DigitigradeDollsSelectTrouserHemAndShoeMaps(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var inventory = entities.System<InventorySystem>();
            var appearance = entities.System<HumanoidAppearanceSystem>();
            try
            {
                foreach (var sex in new[] { Sex.Male, Sex.Female })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(species).WithSex(sex);
                    profile.Appearance.BodyFeatures.Clear();
                    var (markingId, markingState) = species switch
                    {
                        "Vulpkanin" => ("VulpPointsCrestLegs", "points_crest-legs"),
                        "Feroxi" => ("FeroxiLegStripesBlitz", "feroxi-leg-stripes-blitz"),
                        _ => ("TajaranOverlayPatch", "patch"),
                    };
                    profile.Appearance.Markings.Add(new Marking(markingId, [Robust.Shared.Maths.Color.White]));
                    appearance.LoadProfile(uid, profile);
                    foreach (var (prototype, slot, state) in new[]
                    {
                        ("ClothingUniformJumpsuitEngineering", "jumpsuit", sex == Sex.Female ? "jumpsuit-female" : "jumpsuit"),
                        ("ClothingUniformJumpskirtEngineering", "jumpsuit", sex == Sex.Female ? "jumpsuit-female-hem" : "jumpsuit-hem"),
                        ("ClothingShoesBootsLaceup", "shoes", "shoes"),
                        ("ClothingOuterCoatLab", "outerClothing", "outerwear-hem"),
                        ("ClothingOuterHardsuitEngineering", "outerClothing", "outerwear"),
                    })
                    {
                        var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                        try
                        {
                            Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True);
                            var sprite = entities.GetComponent<SpriteComponent>(uid);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out var index), Is.True,
                                $"Missing {species}/{sex}/{prototype}/{slot} displacement.");
                            var layer = (SpriteComponent.Layer) sprite[index];
                            var expected = species == "Feroxi" && slot != "shoes" && !state.EndsWith("hem")
                                ? state + "-feroxi" : state;
                            Assert.That(layer.ActualState!.StateId.ToString(), Is.EqualTo(expected));
                            profile.Appearance.PlantigradeLegs = true;
                            appearance.LoadProfile(uid, profile);
                            var straightBody = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(HumanoidVisualLayers.LLeg)];
                            Assert.That(straightBody.ActualState!.RSI.Path.ToString(), Does.Contain("plantigrade.rsi"));
                            var markingLayer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(markingId + "-" + markingState)];
                            Assert.That(markingLayer.ActualState!.RSI.Path.ToString(), Does.Contain("plantigrade_markings.rsi"));
                            if (slot == "shoes")
                                Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out _), Is.False);
                            else
                            {
                                Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out index), Is.True);
                                var straightState = slot == "outerClothing" ? "outerwear-hem"
                                    : sex == Sex.Female ? "jumpsuit-female-hem" : "jumpsuit-hem";
                                Assert.That(((SpriteComponent.Layer) sprite[index]).ActualState!.StateId.ToString(), Is.EqualTo(straightState));
                            }
                            profile.Appearance.PlantigradeLegs = false;
                            appearance.LoadProfile(uid, profile);
                            markingLayer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(markingId + "-" + markingState)];
                            Assert.That(markingLayer.ActualState!.RSI.Path.ToString(), Does.Not.Contain("plantigrade_markings.rsi"));
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out index), Is.True);
                            Assert.That(((SpriteComponent.Layer) sprite[index]).ActualState!.StateId.ToString(), Is.EqualTo(expected));
                            Assert.That(inventory.TryUnequip(uid, slot, force: true, reparent: false), Is.True);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out _), Is.False);
                        }
                        finally
                        {
                            entities.DeleteEntity(item);
                        }
                    }
                }
            }
            finally
            {
                entities.DeleteEntity(uid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Human", "ClothingUniformJumpsuitEngineering", "engineering.rsi")]
    [TestCase("Vulpkanin", "ClothingUniformJumpsuitMilitaryTurtleneckBlueMercenaryMedic", "military.rsi")]
    public async Task EquipmentAndAppearanceChangesRestoreOriginalLayersWithoutAccumulatingResources(
        string species, string uniformPrototype, string uniformRsi)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity(uniformPrototype, MapCoordinates.Nullspace);
            var coat = entities.SpawnEntity("ClothingOuterCoatLab", MapCoordinates.Nullspace);
            var armor = entities.SpawnEntity("ClothingOuterHardsuitEngineering", MapCoordinates.Nullspace);
            var gloves = entities.SpawnEntity("ClothingHandsGlovesCombat", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var fit = entities.System<ClothingBodyFitSystem>();
                var inventory = entities.System<InventorySystem>();
                var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                var part = BodyCustomizationCatalog.Default(species == "Human" ? "belly" : "butt");
                part.Enabled = true;
                part.Size = species == "Human" ? "10" : "4";
                profile.Appearance.BodyFeatures.Add(part);
                if (species == "Vulpkanin")
                {
                    var breasts = BodyCustomizationCatalog.Default("breasts");
                    breasts.Enabled = true;
                    breasts.Size = "j";
                    profile.Appearance.BodyFeatures.Add(breasts);
                }
                appearance.LoadProfile(uid, profile);
                Assert.That(inventory.TryEquip(uid, gloves, "gloves", force: true), Is.True);
                Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var clothingLayer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet("jumpsuit-0")];
                var gloveLayer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet("gloves-0")];
                var gloveScale = gloveLayer.Scale;
                var gloveState = gloveLayer.ActualState;
                Assert.That(clothingLayer.ShaderPrototype, Is.EqualTo("DeepLagoonClothingBodyFit"));
                var fittedScale = clothingLayer.Scale;
                var count = sprite.AllLayers.Count();
                var shader = clothingLayer.Shader!;
                fit.Refresh(uid, humanoid, sprite);
                // ClydeHeadless.Dispose is a no-op; verify replacement and balanced layer ownership here.
                Assert.That(clothingLayer.Shader, Is.Not.SameAs(shader));
                Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                Assert.That(clothingLayer.Scale, Is.EqualTo(fittedScale));
                Assert.That(gloveLayer.Scale, Is.EqualTo(gloveScale));
                Assert.That(gloveLayer.ActualState, Is.SameAs(gloveState));
                Assert.That(sprite.LayerMapTryGet("gloves-0-body-fit-source", out _), Is.False,
                    "The fixed hand mask must not deform gloves along with the uniform.");
                Assert.That(clothingLayer.ActualState!.RSI.Path.ToString(), Does.Contain(uniformRsi));
                Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out var helper), Is.True);
                Assert.That(((SpriteComponent.Layer) sprite[helper]).CopyToShaderParameters!.LayerKey, Is.EqualTo("jumpsuit-0"));
                Assert.That(inventory.TryEquip(uid, coat, "outerClothing", force: true), Is.True);
                Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet("outerClothing-0")]).ShaderPrototype,
                    Is.EqualTo("DeepLagoonClothingBodyFit"));
                Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true), Is.True);
                Assert.That(sprite.LayerMapTryGet("outerClothing-0-body-fit-source", out _), Is.False);
                Assert.That(inventory.TryEquip(uid, armor, "outerClothing", force: true), Is.True);
                Assert.That(sprite.LayerMapTryGet("outerClothing-0-body-fit-source", out _), Is.False);
                Assert.That(entities.GetComponent<ClothingComponent>(armor).BodyFit, Is.EqualTo(ClothingBodyFit.Rigid));
                Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true), Is.True);
                foreach (var feature in profile.Appearance.BodyFeatures) feature.Visibility = 2;
                appearance.LoadProfile(uid, profile);
                Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.False,
                    "Anatomy explicitly drawn above clothing must not deform it.");
                Assert.That(clothingLayer.Scale, Is.EqualTo(fittedScale / 2));
                foreach (var feature in profile.Appearance.BodyFeatures) feature.Visibility = 1;
                appearance.LoadProfile(uid, profile);
                Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.True);
                Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true), Is.True);
                Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.False);
                Assert.That(sprite.LayerMapTryGet("jumpsuit-0", out _), Is.False);
            }
            finally
            {
                entities.DeleteEntity(uid);
                entities.DeleteEntity(uniform);
                entities.DeleteEntity(coat);
                entities.DeleteEntity(armor);
                entities.DeleteEntity(gloves);
            }
        });
        await pair.CleanReturnAsync();
    }
}
