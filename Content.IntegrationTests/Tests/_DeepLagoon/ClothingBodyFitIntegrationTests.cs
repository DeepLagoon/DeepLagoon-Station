using System.Linq;
using System.Collections;
using System.Reflection;
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
    [TestCase("Human")]
    [TestCase("Synth")]
    public async Task EveryBodyFeatureKeepsItsBareSpriteHiddenUnderClothing(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitMercenary", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var inventory = entities.System<InventorySystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                foreach (var part in BodyCustomizationCatalog.Order.Where(part => part != "womb"))
                foreach (var shape in BodyCustomizationCatalog.Parts[part].Keys.Where(shape => part != "anus" || shape == "donut"))
                {
                    var editorSizes = BodyCustomizationCatalog.EditorSizes(part, shape);
                    var sizes = editorSizes.Where(size => size != "0").Take(2).Concat(editorSizes.TakeLast(1))
                        .Concat(BodyCustomizationCatalog.Sizes(part, shape).TakeLast(1)).Distinct();
                    foreach (var size in sizes)
                    {
                        var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                        var feature = BodyCustomizationCatalog.Default(part);
                        feature.Enabled = true; feature.Shape = shape; feature.Size = size;
                        feature.MatchSkin = false; feature.Color = "#FF0000";
                        feature.SecondaryEnabled = part == "breasts"; feature.SecondaryColor = "#FF0000";
                        profile.Appearance.BodyFeatures.Add(feature);
                        appearance.LoadProfile(uid, profile);
                        var sides = BodyCustomizationCatalog.Parts[part][shape][size].ToList();
                        if (part == "breasts" && shape == "pair" && BodyCustomizationCatalog.BreastSecondarySizes.Contains(size)) sides.Add("SECONDARY");
                        void Check(bool visible)
                        {
                            foreach (var side in sides)
                            {
                                var layer = sprite[sprite.LayerMapGet($"dl-body/{part}/{side}")];
                                Assert.That(layer.Visible, Is.EqualTo(visible), $"{species}/{part}/{shape}/{size}/{side}");
                                Assert.That(layer.Color, Is.EqualTo(Robust.Shared.Maths.Color.Red));
                            }
                        }
                        Check(true);
                        Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                        Check(false);
                        Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(uid).BodyFeatures.Single().Size, Is.EqualTo(size));
                        Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                        Check(true);
                    }
                }
            }
            finally { entities.DeleteEntity(uid); entities.DeleteEntity(uniform); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SynthShellsProvideBodyAndHandMasksForClothingFit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>();
            var uid = entities.SpawnEntity("MobSynthDummy", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitMercenary", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var inventory = entities.System<InventorySystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                foreach (var shell in new[] { "Human", "Mechanical", "Feroxi" })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
                    foreach (var part in new[] { "Chest", "LLeg", "RLeg", "LHand", "RHand" })
                        profile.Appearance.Markings.Add(prototypes.Index<MarkingPrototype>($"Synth{shell}{part}").AsMarking());
                    foreach (var (part, size) in new[] { ("breasts", "j"), ("butt", "4") })
                    {
                        var feature = BodyCustomizationCatalog.Default(part);
                        feature.Enabled = true; feature.Size = size;
                        profile.Appearance.BodyFeatures.Add(feature);
                    }
                    appearance.LoadProfile(uid, profile);
                    var chest = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(HumanoidVisualLayers.Chest)];
                    Assert.That(chest.ActualState, Is.Null, "Synth shells must remain markings rather than restoring a hidden human base body.");
                    Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.True, $"Missing clothing fit for {shell} shell.");
                    Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet("jumpsuit-0")]).ShaderPrototype,
                        Is.EqualTo("DeepLagoonClothingBodyFit"));
                    if (shell == "Feroxi")
                    {
                        Assert.That(sprite.LayerMapTryGet("jumpsuit-0-displacement", out var displacement), Is.True);
                        Assert.That(sprite[displacement].Visible, Is.False, "The species displacement must be composed into the fit map.");
                    }
                }
            }
            finally { entities.DeleteEntity(uid); entities.DeleteEntity(uniform); }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Human")]
    [TestCase("Synth")]
    public async Task DisabledClothingFitHidesEveryOrganLayerUntilTorsoClothesAreRemoved(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitMercenary", MapCoordinates.Nullspace);
            var coat = entities.SpawnEntity("ClothingOuterCoatLab", MapCoordinates.Nullspace);
            var armor = entities.SpawnEntity("ClothingOuterHardsuitEngineering", MapCoordinates.Nullspace);
            var gloves = entities.SpawnEntity("ClothingHandsGlovesCombat", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var inventory = entities.System<InventorySystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                void Check(bool visible)
                {
                    foreach (var side in new[] { "FRONT", "BEHIND", "SECONDARY" })
                        Assert.That(sprite[sprite.LayerMapGet($"dl-body/breasts/{side}")].Visible, Is.EqualTo(visible), $"{species}/{side}");
                    Assert.That(sprite[sprite.LayerMapGet("dl-body/butt/FRONT")].Visible, Is.True, "Other parts retain their own visibility preference.");
                }
                foreach (var visibility in new[] { 1, 2 })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                    var feature = BodyCustomizationCatalog.Default("breasts");
                    feature.Enabled = true; feature.Size = "j"; feature.SecondaryEnabled = true;
                    feature.ShowThroughClothing = false; feature.Visibility = visibility;
                    profile.Appearance.BodyFeatures.Add(feature);
                    var other = BodyCustomizationCatalog.Default("butt");
                    other.Enabled = true; other.Size = "4"; other.Visibility = 2;
                    profile.Appearance.BodyFeatures.Add(other);
                    appearance.LoadProfile(uid, profile);
                    Check(true);
                    Assert.That(inventory.TryEquip(uid, gloves, "gloves", force: true), Is.True);
                    Check(true);
                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                    Check(false);
                    Assert.That(inventory.TryEquip(uid, coat, "outerClothing", force: true), Is.True);
                    Check(false);
                    Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                    Check(false);
                    Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true, reparent: false), Is.True);
                    Check(true);
                    Assert.That(inventory.TryEquip(uid, armor, "outerClothing", force: true), Is.True);
                    Check(false);
                    Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true, reparent: false), Is.True);
                    Check(true);
                    Assert.That(inventory.TryUnequip(uid, "gloves", force: true, reparent: false), Is.True);
                }
            }
            finally
            {
                entities.DeleteEntity(uid); entities.DeleteEntity(uniform); entities.DeleteEntity(coat);
                entities.DeleteEntity(armor); entities.DeleteEntity(gloves);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Human")]
    [TestCase("Synth")]
    public async Task ClothingCapsAndCheckboxUseSeparateSilhouettesWithoutChangingBareAnatomy(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitMercenary", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var inventory = entities.System<InventorySystem>();
                var fit = entities.System<ClothingBodyFitSystem>();
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                object FittedMap()
                {
                    var fits = (IDictionary) typeof(ClothingBodyFitSystem).GetField("_fits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fit)!;
                    var slot = ((IEnumerable) fits[uid]!).Cast<object>().Single(f => (string) f.GetType().GetProperty("Key")!.GetValue(f)! == "jumpsuit-0");
                    return slot.GetType().GetProperty("Map")!.GetValue(slot)!;
                }
                foreach (var (part, cap, oversized) in new[] { ("butt", "4", "8"), ("breasts", "j", "impossible") })
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                    var feature = BodyCustomizationCatalog.Default(part);
                    feature.Enabled = true; feature.Size = cap;
                    feature.MatchSkin = false; feature.Color = "#FF0000";
                    profile.Appearance.BodyFeatures.Add(feature);
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.True);
                    var cappedMap = FittedMap();
                    feature.Size = oversized;
                    appearance.LoadProfile(uid, profile);
                    Assert.That(FittedMap(), Is.SameAs(cappedMap), "Runtime growth must not increase the editor-capped clothing silhouette.");
                    var bodyLayer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")];
                    Assert.That(bodyLayer.ActualState!.StateId.ToString(), Is.EqualTo(BodyCustomizationCatalog.SpriteState(feature, "FRONT")));
                    Assert.That(bodyLayer.Visible, Is.False, "The full-size red skin must not protrude past the compressed clothing silhouette in any direction.");
                    feature.ShowThroughClothing = false;
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.False);
                    Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(uid).BodyFeatures.Single().Size, Is.EqualTo(oversized));
                    feature.ShowThroughClothing = true;
                    feature.Size = part == "breasts" ? "a" : "1";
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite.LayerMapTryGet("jumpsuit-0-body-fit-source", out _), Is.False, "The smallest part must not deform clothing.");
                    Assert.That(sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")].Visible, Is.False, "Size 1/A must also hide its raw skin while clothed.");
                    feature.Size = part == "breasts" ? "b" : "2";
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")].Visible, Is.False);
                    Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                    Assert.That(sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")].Visible, Is.True, "Removing clothing must restore the original colored anatomy.");
                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                    Assert.That(sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")].Visible, Is.False);
                    feature.Visibility = 2;
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite[sprite.LayerMapGet($"dl-body/{part}/FRONT")].Visible, Is.True, "Explicitly exposed anatomy retains its separate above-clothing mode.");
                }
                var alternateProfile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                var alternate = BodyCustomizationCatalog.Default("testicles");
                alternate.Enabled = true; alternate.Size = "4";
                alternateProfile.Appearance.BodyFeatures.Add(alternate);
                appearance.LoadProfile(uid, alternateProfile);
                var maps = (IDictionary) typeof(ClothingBodyFitSystem).GetField("_maps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fit)!;
                Assert.That(maps.Keys.Cast<string>().Any(key => key.Contains("testicles_single_3_0_BEHIND")), Is.True,
                    "A compressed shape must retain its own rear layer even if the bare size has no rear layer.");
            }
            finally
            {
                entities.DeleteEntity(uid);
                entities.DeleteEntity(uniform);
            }
        });
        await pair.CleanReturnAsync();
    }

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
