using System;
using System.Collections.Generic;
using System.Linq;
using Content.Client.Humanoid;
using Content.Shared.DisplacementMap;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpeciesUnderwearMapTests
{
    [Test]
    public async Task NativeUnderwearMapsReachEquipmentLayersAndRespectLegShape()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var appearance = entities.System<HumanoidAppearanceSystem>();
            var inventory = entities.System<InventorySystem>();
            foreach (var species in new[] { "Reptilian", "Vulpkanin", "Tajaran", "Synth", "Feroxi", "Harpy", "Hydrakin", "Ariral" })
            {
                var definition = prototypes.Index<SpeciesPrototype>(species);
                var (path, state) = species switch
                {
                    "Feroxi" => ("_DeepLagoon/Mobs/Digitigrade/displacement.rsi", "jumpsuit-feroxi"),
                    "Harpy" => ("_DV/Mobs/Species/Harpy/displacement.rsi", "jumpsuit"),
                    "Hydrakin" => ("_Obelisk/Mobs/Species/Hydrakin/displacement.rsi", "jumpsuit"),
                    _ => ("_White/Mobs/Species/displacement.rsi", "jumpsuit"),
                };
                foreach (var prototypeId in new[] { definition.Prototype, definition.DollPrototype })
                {
                    var prototype = prototypes.Index(prototypeId);
                    var authored = (InventoryComponent) prototype.Components["Inventory"].Component;
                    CheckPolicy(authored.Displacements, path, state, $"{species}/{prototypeId}/default");
                    CheckPolicy(authored.MaleDisplacements.Count > 0 ? authored.MaleDisplacements : authored.Displacements,
                        path, state, $"{species}/{prototypeId}/male");
                    CheckPolicy(authored.FemaleDisplacements.Count > 0 ? authored.FemaleDisplacements : authored.Displacements,
                        path, state, $"{species}/{prototypeId}/female");
                }

                var uid = entities.SpawnEntity(definition.DollPrototype, MapCoordinates.Nullspace);
                var top = entities.SpawnEntity("ClothingUnderTopBraGrey", MapCoordinates.Nullspace);
                var bottom = entities.SpawnEntity("ClothingUnderBottomGrey", MapCoordinates.Nullspace);
                var genericBottom = entities.SpawnEntity("ClothingUnderBottomFBriefs", MapCoordinates.Nullspace);
                try
                {
                    foreach (var sex in new[] { Sex.Male, Sex.Female })
                    foreach (var plantigrade in definition.MarkingLegShape || definition.PlantigradeSprites != null
                                 ? new[] { false, true } : new[] { false })
                    {
                        var label = $"{species}/{sex}/plantigrade={plantigrade}";
                        var profile = HumanoidCharacterProfile.DefaultWithSpecies(species).WithSex(sex);
                        profile.Appearance.BodyFeatures.Clear();
                        profile.Appearance.PlantigradeLegs = plantigrade;
                        if (definition.MarkingLegShape)
                        {
                            profile.Appearance.Markings.Clear();
                            var shell = plantigrade ? "Human" : "Vulpkanin";
                            foreach (var leg in new[] { "LLeg", "RLeg", "LFoot", "RFoot" })
                                profile.Appearance.Markings.Add(new Marking($"Synth{shell}{leg}", [Robust.Shared.Maths.Color.White]));
                        }
                        appearance.LoadProfile(uid, profile);
                        var sprite = entities.GetComponent<SpriteComponent>(uid);
                        var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                        if (definition.MarkingLegShape || definition.PlantigradeSprites != null)
                            Assert.That(appearance.UsesPlantigradeClothing(humanoid), Is.EqualTo(plantigrade), label);
                        var initialLayers = sprite.AllLayers.Count();
                        foreach (var (item, slot) in new[] { (top, "underweartop"), (bottom, "underwearbottom"), (genericBottom, "underwearbottom") })
                        {
                            Assert.That(inventory.TryEquip(uid, item, slot, force: true), Is.True, label + "/" + slot);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0", out _), Is.True, label + "/" + slot);
                            var hasMap = sprite.LayerMapTryGet(slot + "-0-displacement", out var index);
                            // An authored native garment already has the correct species shape;
                            // marking-defined plantigrade shells use the original human shape.
                            // Ariral deliberately shares the reptilian equipment species ID.
                            var nativeGarment = species is "Reptilian" or "Ariral" && item == bottom;
                            var bypass = definition.MarkingLegShape && plantigrade || nativeGarment;
                            Assert.That(hasMap, Is.EqualTo(!bypass), label + "/" + slot);
                            if (nativeGarment)
                                Assert.That(((SpriteComponent.Layer) sprite[sprite.LayerMapGet(slot + "-0")]).ActualState!.StateId.ToString(),
                                    Is.EqualTo("equipped-UNDERWEARBOTTOM-reptilian"), label + "/native garment");
                            if (!bypass)
                            {
                                var layer = (SpriteComponent.Layer) sprite[index];
                                Assert.That(layer.ActualState, Is.Not.Null, label);
                                Assert.That(layer.ActualState!.StateId.ToString(), Is.EqualTo(state), label);
                                Assert.That(layer.ActualState.RSI.Path.ToString(), Does.EndWith(path), label);
                                Assert.That(layer.CopyToShaderParameters!.LayerKey, Is.EqualTo(slot + "-0"), label);
                            }
                            Assert.That(inventory.TryUnequip(uid, slot, force: true, reparent: false), Is.True, label);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0", out _), Is.False, label);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-displacement", out _), Is.False, label);
                            Assert.That(sprite.LayerMapTryGet(slot + "-0-body-fit-source", out _), Is.False, label);
                            Assert.That(sprite.AllLayers.Count(), Is.EqualTo(initialLayers), label + "/cleanup");
                        }
                    }
                }
                finally
                {
                    entities.DeleteEntity(uid);
                    entities.DeleteEntity(top);
                    entities.DeleteEntity(bottom);
                    entities.DeleteEntity(genericBottom);
                }
            }
        });
        await pair.CleanReturnAsync();
    }

    private static void CheckPolicy(Dictionary<string, DisplacementData> policy, string path, string state, string label)
    {
        foreach (var slot in new[] { "underweartop", "underwearbottom" })
        {
            Assert.That(policy.ContainsKey(slot), Is.True, label + "/" + slot);
            var data = policy[slot];
            Assert.That(data.SizeMaps.ContainsKey(32), Is.True, label + "/" + slot);
            Assert.That(data.SizeMaps[32].RsiPath, Is.EqualTo(path), label + "/" + slot);
            Assert.That(data.SizeMaps[32].State, Is.EqualTo(state), label + "/" + slot);
            Assert.That(data.HemMaps, Is.Empty, label + "/underwear must not inherit a full trouser hem");
        }
    }
}
