using System.Linq;
using Content.Client._DeepLagoon.BodyCustomization;
using Content.Client.Humanoid;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ClothingFaceFitIntegrationTests
{
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
                    ("ClothingHeadHelmetHardsuitEngineering", "head"),
                    ("Cigarette", "mask"), ("Cigar", "mask"), ("SmokingPipe", "mask"),
                })
                {
                    var item = entities.SpawnEntity(prototype, MapCoordinates.Nullspace);
                    try
                    {
                        Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True);
                        var expected = slot == "head" || prototype == "ClothingMaskGas";
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
                        if (slot == "mask")
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
