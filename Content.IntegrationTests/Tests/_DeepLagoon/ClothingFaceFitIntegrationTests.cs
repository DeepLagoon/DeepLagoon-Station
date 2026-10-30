using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Client._DeepLagoon.Tattoos;
using Content.Client.Humanoid;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ClothingFaceFitIntegrationTests
{
    [TestPrototypes]
    private const string SpeciesDummy = """
        - type: entity
          id: ClothingFaceFitSpeciesVulpDummy
          parent: MobVulpkaninDummy
          components:
          - type: Inventory
            speciesId: vulpkanin
        """;

    [TestCase(Sex.Male, false)]
    [TestCase(Sex.Female, false)]
    [TestCase(Sex.Male, true)]
    [TestCase(Sex.Female, true)]
    public async Task StandardVulpWornGasMuzzleAndSeparateBalaclavaCoverTheProtrudingNose(Sex sex, bool speciesVisuals)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity(speciesVisuals ? "ClothingFaceFitSpeciesVulpDummy" : "MobVulpkaninDummy", MapCoordinates.Nullspace);
            var inventory = entities.System<InventorySystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Vulpkanin").WithSex(sex);
            profile.Appearance.BodyFeatures.Clear();
            profile.Appearance.Markings.Clear();
            entities.System<HumanoidAppearanceSystem>().LoadProfile(uid, profile);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var head = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(HumanoidVisualLayers.Head)];
            var cache = pair.Client.ResolveDependency<IResourceCache>();
            var reference = cache.GetResource<RSIResource>(new ResPath("/Textures/Mobs/Species/Human/parts.rsi")).RSI;
            Assert.That(reference.TryGetState(sex == Sex.Female ? "head_f" : "head_m", out var referenceState), Is.True);
            var maskState = speciesVisuals ? "equipped-MASK-vulpkanin" : "equipped-MASK";
            try
            {
                foreach (var (prototype, slot, state) in new[]
                {
                    ("ClothingMaskGas", "mask", maskState),
                    ("ClothingMaskMuzzle", "mask", maskState),
                    ("ClothingBalaclavaSkiMaskGreen", "balaclava", "equipped-BALACLAVA"),
                })
                {
                    var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                    try
                    {
                        Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True);
                        var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(slot + "-0")];
                        Assert.That(layer.ActualState!.StateId.ToString(), Is.EqualTo(state), "Test the state selected by real equipment rendering.");
                        Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.True, prototype);
                        for (var direction = 0; direction < 4; direction++)
                        {
                            using var human = TattooSpriteFrames.Read(referenceState!, direction);
                            using var face = TattooSpriteFrames.Read(head.ActualState!, direction);
                            using var garment = TattooSpriteFrames.Read(layer.ActualState!, direction);
                            using var map = ClothingFaceFitMap.Build(human, face, garment, null, 16, coversMouth: true);
                            for (var y = 5; y < 14; y++)
                            for (var x = 0; x < 32; x++)
                            {
                                if (face[x, y].A < 128 || human[x, y].A >= 128) continue;
                                var alpha = (int) garment[x, y].A;
                                if (map != null)
                                {
                                    var point = map[x + 16, y + 16];
                                    alpha = garment[point.R, point.G].A * point.A / 255;
                                }
                                Assert.That(alpha, Is.GreaterThanOrEqualTo(128),
                                    $"{prototype}/{sex}/{direction}: bare muzzle at {x},{y}.");
                            }
                        }
                    }
                    finally
                    {
                        inventory.TryUnequip(uid, slot, force: true, reparent: false);
                        Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.False);
                        entities.DeleteEntity(item);
                    }
                }

                var gas = entities.SpawnEntity("ClothingMaskGas", MapCoordinates.Nullspace);
                var balaclava = entities.SpawnEntity("ClothingBalaclavaSkiMaskGreen", MapCoordinates.Nullspace);
                try
                {
                    Assert.That(inventory.TryEquip(uid, gas, "mask", force: true), Is.True);
                    Assert.That(inventory.TryEquip(uid, balaclava, "balaclava", force: true), Is.True);
                    var count = sprite.AllLayers.Count();
                    for (var i = 0; i < 3; i++)
                        entities.System<ClothingBodyFitSystem>().Refresh(uid, entities.GetComponent<HumanoidAppearanceComponent>(uid), sprite);
                    Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                    Assert.That(sprite.LayerMapTryGet("mask-0-body-fit-source", out _), Is.True);
                    Assert.That(sprite.LayerMapTryGet("balaclava-0-body-fit-source", out _), Is.True);
                    inventory.TryUnequip(uid, "mask", force: true, reparent: false);
                    Assert.That(sprite.LayerMapTryGet("mask-0-body-fit-source", out _), Is.False);
                    Assert.That(sprite.LayerMapTryGet("balaclava-0-body-fit-source", out _), Is.True);
                }
                finally { entities.DeleteEntity(gas); entities.DeleteEntity(balaclava); }
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Vulpkanin", "VulpSnout", null)]
    [TestCase("Feroxi", "FeroxiSnout", null)]
    [TestCase("Synth", "SynthVulpSnout", "SynthVulpkaninHead")]
    [TestCase("Synth", "BlueMoonSnoutsMam_snoutsNoodleSnout", "SynthHumanHead")]
    [TestCase("Synth", "BlueMoonSnoutsMam_snoutsSynthlizSynthlizvenomSnout", "SynthHumanHead")]
    public async Task AppearanceHeadAndSnoutFitMasksAndHelmetsAndRestoreOnChanges(
        string species, string snout, string shell)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var uid = entities.SpawnEntity($"Mob{species}Dummy", MapCoordinates.Nullspace);
            var inventory = entities.System<InventorySystem>();
            var appearance = entities.System<HumanoidAppearanceSystem>();
            var fit = entities.System<ClothingBodyFitSystem>();
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
            profile.Appearance.BodyFeatures.Clear();
            profile.Appearance.Markings.Clear();
            if (shell != null) profile.Appearance.Markings.Add(new Marking(shell, [Robust.Shared.Maths.Color.White]));
            profile.Appearance.Markings.Add(new Marking(snout, 4));
            appearance.LoadProfile(uid, profile);
            try
            {
                foreach (var (prototype, slot) in new[]
                {
                    ("ClothingMaskGas", "mask"),
                    ("ClothingMaskMuzzle", "mask"),
                    ("ClothingBalaclavaSkiMaskGreen", "balaclava"),
                    ("ClothingHeadHelmetHardsuitEngineering", "head"),
                    ("Cigarette", "mask"), ("Cigar", "mask"), ("SmokingPipe", "mask"),
                })
                {
                    var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                    try
                    {
                        Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True);
                        var expected = prototype is not ("Cigarette" or "Cigar" or "SmokingPipe");
                        bool HasFit() => sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _);
                        Assert.That(HasFit(), Is.EqualTo(expected), prototype);
                        if (!expected) continue;
                        var count = sprite.AllLayers.Count();
                        var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(slot + "-0")];
                        var scale = layer.Scale;
                        for (var i = 0; i < 3; i++) fit.Refresh(uid, entities.GetComponent<HumanoidAppearanceComponent>(uid), sprite);
                        Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
                        Assert.That(layer.Scale, Is.EqualTo(scale));
                        Assert.That(layer.ShaderPrototype, Is.EqualTo("DeepLagoonClothingBodyFit"));
                        if (slot == "mask" && entities.HasComponent<MaskComponent>(item))
                        {
                            entities.System<MaskSystem>().SetToggled(item, true);
                            Assert.That(HasFit(), Is.False, "A lowered mask must release the snout.");
                            entities.System<MaskSystem>().SetToggled(item, false);
                            Assert.That(HasFit(), Is.True);
                        }
                        appearance.LoadProfile(uid, HumanoidCharacterProfile.DefaultWithSpecies("Human"));
                        Assert.That(HasFit(), Is.False, "Changing appearance must discard the old face map.");
                        appearance.LoadProfile(uid, profile);
                        Assert.That(HasFit(), Is.True);
                    }
                    finally
                    {
                        inventory.TryUnequip(uid, slot, force: true, reparent: false);
                        Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.False);
                        entities.DeleteEntity(item);
                    }
                }
            }
            finally { entities.DeleteEntity(uid); }
        });
        await pair.CleanReturnAsync();
    }
}
