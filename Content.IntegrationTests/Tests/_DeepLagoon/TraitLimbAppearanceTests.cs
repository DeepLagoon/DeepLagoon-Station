using System.Linq;
using Content.Server.Traits;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Movement.Components;
using Content.Shared.Prying.Components;
using Content.Shared.Traits;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class TraitLimbAppearanceTests
{
    [TestCase("Human")]
    [TestCase("Vulpkanin")]
    public async Task ProstheticTraitsPreserveLimbsAndMarkings(string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var mob = entities.SpawnEntity("Mob" + species, map.GridCoords);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(mob);
            var appearance = entities.System<SharedHumanoidAppearanceSystem>();
            var body = entities.System<SharedBodySystem>();
            var layers = new[]
            {
                HumanoidVisualLayers.LArm, HumanoidVisualLayers.RArm,
                HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand,
                HumanoidVisualLayers.LLeg, HumanoidVisualLayers.RLeg,
                HumanoidVisualLayers.LFoot, HumanoidVisualLayers.RFoot,
            };
            var color = Color.FromHex("#4275A1");
            // Preserve the native species, plus a custom hand sprite and per-limb tint.
            foreach (var layer in layers)
                appearance.SetBaseLayerColor(mob, layer, color);
            appearance.SetBaseLayerId(mob, HumanoidVisualLayers.LHand, "MobVulpkaninLHand");
            foreach (var category in new[] { MarkingCategories.Arms, MarkingCategories.Legs })
            {
                var marking = prototypes.EnumeratePrototypes<MarkingPrototype>().First(p => p.MarkingCategory == category).AsMarking();
                marking.DrawOrder = 7;
                marking.Unshaded = true;
                humanoid.MarkingSet.Markings[category] = new() { marking };
            }
            var markings = humanoid.MarkingSet.Markings
                .Where(p => p.Key is MarkingCategories.Arms or MarkingCategories.Legs)
                .ToDictionary(p => p.Key, p => p.Value.Select(m => new Marking(m)).ToList());

            foreach (var function in prototypes.Index<TraitPrototype>("BionicPryArm").Functions)
                function.OnPlayerSpawn(mob, pair.Server.ResolveDependency<IComponentFactory>(), entities,
                    pair.Server.ResolveDependency<ISerializationManager>());
            entities.AddComponent<BionicLegsComponent>(mob);

            Assert.That(entities.GetComponent<PryingComponent>(mob).PryPowered, Is.False);
            Assert.That(entities.GetComponent<PryingComponent>(mob).SpeedModifier, Is.EqualTo(1.5f));
            foreach (var layer in layers)
            {
                var expected = layer == HumanoidVisualLayers.LHand ? "MobVulpkaninLHand" : "Mob" + species + layer;
                Assert.That(humanoid.CustomBaseLayers[layer].Id, Is.EqualTo(expected), layer.ToString());
                Assert.That(humanoid.CustomBaseLayers[layer].Color, Is.EqualTo(color), layer.ToString());
                var part = body.GetBodyChildren(mob).Single(p => p.Component.ToHumanoidLayers() == layer);
                Assert.That(entities.GetComponent<BodyPartAppearanceComponent>(part.Id).ID?.Id, Is.EqualTo(expected));
            }
            foreach (var (category, expected) in markings)
            {
                var actual = humanoid.MarkingSet.Markings[category];
                Assert.That(actual.Select(m => m.MarkingId), Is.EqualTo(expected.Select(m => m.MarkingId)));
                Assert.That(actual.Single().DrawOrder, Is.EqualTo(7));
                Assert.That(actual.Single().Unshaded, Is.True);
                Assert.That(actual.Single().MarkingColors, Is.EqualTo(expected.Single().MarkingColors));
            }
            foreach (var leg in body.GetBodyChildrenOfType(mob, BodyPartType.Leg))
            {
                Assert.That(entities.GetComponent<MetaDataComponent>(leg.Id).EntityPrototype!.ID,
                    Does.StartWith("Speed"));
                Assert.That(entities.GetComponent<MovementBodyPartComponent>(leg.Id).SprintSpeed, Is.EqualTo(5.175f));
            }
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }
}
