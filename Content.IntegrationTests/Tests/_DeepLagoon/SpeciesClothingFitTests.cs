using System.Linq;
using System.Numerics;
using Content.Client.Humanoid;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpeciesClothingFitTests
{
    [TestCase("Human", Sex.Female)]
    [TestCase("Vox", Sex.Unsexed)]
    [TestCase("Resomi", Sex.Female)]
    [TestCase("Resomi", Sex.Male)]
    public async Task SpeciesMapsSurviveBodySizeHeightAndClothingChanges(string species, Sex sex)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            foreach (var id in new[] { "Mob" + species, "Mob" + species + "Dummy" })
            {
                Assert.That(prototypes.Index<EntityPrototype>(id).TryComp<InventoryComponent>(out var protoInventory, entities.ComponentFactory), Is.True);
                if (species == "Human") continue;
                foreach (var slot in new[] { "underweartop", "underwearbottom" })
                {
                    Assert.That(protoInventory!.Displacements.ContainsKey(slot), Is.True, id + "/" + slot);
                    if (species == "Resomi")
                        Assert.That(protoInventory.FemaleDisplacements.ContainsKey(slot), Is.True, id + "/female/" + slot);
                }
                Assert.That(protoInventory!.Displacements.ContainsKey("bodyFeatures"), Is.True, id + "/bodyFeatures");
                Assert.That(protoInventory.Displacements["bodyFeatures"].SizeMaps[32].RsiPath.ToString(),
                    Does.Contain("BodyCustomization/species_displacement.rsi"), "Anatomy uses a volume-preserving map, not a garment cut-out.");
            }
            var uid = entities.SpawnEntity("Mob" + species + "Dummy", MapCoordinates.Nullspace);
            var top = entities.SpawnEntity("ClothingUnderTopBraGrey", MapCoordinates.Nullspace);
            var bottom = entities.SpawnEntity("ClothingUnderBottomGrey", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitEngineering", MapCoordinates.Nullspace);
            var coat = entities.SpawnEntity("ClothingOuterCoatLab", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var inventory = entities.System<InventorySystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                var profile = HumanoidCharacterProfile.DefaultWithSpecies(species).WithSex(sex);
                appearance.LoadProfile(uid, profile);
                Assert.That(inventory.TryEquip(uid, top, "underweartop", force: true), Is.True);
                Assert.That(inventory.TryEquip(uid, bottom, "underwearbottom", force: true), Is.True);
                foreach (var shaped in new[] { false, true })
                {
                    profile.Appearance.BodyFeatures.Clear();
                    if (shaped)
                        foreach (var (part, size) in new[] { ("breasts", "j"), ("butt", "4") })
                        {
                            var feature = BodyCustomizationCatalog.Default(part); feature.Enabled = true; feature.Size = size;
                            profile.Appearance.BodyFeatures.Add(feature);
                        }
                    profile.Appearance.Height = profile.Appearance.Width = 1;
                    appearance.LoadProfile(uid, profile);
                    var baseScale = sprite.Scale;
                    var localScales = new Vector2[2];
                    var first = true;
                    foreach (var height in new[] { .8f, 1f, 1.2f })
                    {
                        var width = height == .8f ? 1.2f : height == 1.2f ? .8f : 1f;
                        profile.Appearance.Height = height; profile.Appearance.Width = width;
                        appearance.LoadProfile(uid, profile);
                        Assert.That(sprite.Scale.X, Is.EqualTo(baseScale.X * width).Within(.0001f));
                        Assert.That(sprite.Scale.Y, Is.EqualTo(baseScale.Y * height).Within(.0001f));
                        var count = sprite.AllLayers.Count();
                        if (shaped)
                            foreach (var part in new[] { "breasts", "butt" })
                            {
                                var bodyKey = "dl-body/" + part + "/FRONT";
                                var anatomy = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(bodyKey)];
                                Assert.That(anatomy.Scale, Is.EqualTo(Vector2.One), "Species anatomy maps must not alter the local matrix used by clothing fit.");
                                Assert.That(sprite.LayerMapTryGet(bodyKey + "-body-fit-source", out _), Is.EqualTo(species != "Human"));
                            }
                        var index = 0;
                        foreach (var slot in new[] { "underweartop", "underwearbottom" })
                        {
                            var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(slot + "-0")];
                            if (first) localScales[index] = layer.Scale;
                            Assert.That(layer.Scale, Is.EqualTo(localScales[index++]), "Profile scale must affect the whole sprite, not repeatedly scale fitted clothing layers.");
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out var displacement), Is.EqualTo(species != "Human"), slot);
                            if (species != "Human")
                            {
                                var map = (SpriteComponent.Layer) sprite[displacement];
                                Assert.That(map.ActualState!.StateId.Name, Is.EqualTo("jumpsuit"));
                                Assert.That(map.ActualState.RSI.Path.ToString(), Does.Contain("/Species/" + species + "/displacement.rsi"));
                            }
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.EqualTo(shaped), slot);
                        }
                        first = false;
                        Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                        Assert.That(inventory.TryEquip(uid, coat, "outerClothing", force: true), Is.True);
                        Assert.That(sprite.LayerMapTryGet("jumpsuit-0-displacement", out var uniformMap), Is.True);
                        Assert.That(((SpriteComponent.Layer) sprite[uniformMap]).ActualState!.StateId.Name,
                            Is.EqualTo(species == "Human" ? "jumpsuit-female" : "jumpsuit"));
                        if (species == "Vox")
                        {
                            Assert.That(sprite.LayerMapTryGet("outerClothing-0-displacement", out _), Is.False,
                                "The authored Vox lab-coat variant must bypass a second native displacement.");
                            Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet("outerClothing-0")]).ActualState!.StateId.Name,
                                Is.EqualTo("equipped-OUTERCLOTHING-vox"));
                        }
                        else if (species != "Human")
                        {
                            Assert.That(sprite.LayerMapTryGet("outerClothing-0-displacement", out var coatMap), Is.True);
                            Assert.That(((SpriteComponent.Layer) sprite[coatMap]).ActualState!.StateId.Name, Is.EqualTo("outerClothing"));
                        }
                        Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true, reparent: false), Is.True);
                        Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                        Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count), "Changing covering clothes must not leak fit helpers.");
                    }
                }
                foreach (var slot in new[] { "underweartop", "underwearbottom" })
                {
                    Assert.That(inventory.TryUnequip(uid, slot, force: true, reparent: false), Is.True);
                    Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out _), Is.False);
                    Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.False);
                }
            }
            finally { entities.DeleteEntity(uid); entities.DeleteEntity(top); entities.DeleteEntity(bottom); entities.DeleteEntity(uniform); entities.DeleteEntity(coat); }
        });
        await pair.CleanReturnAsync();
    }
}
