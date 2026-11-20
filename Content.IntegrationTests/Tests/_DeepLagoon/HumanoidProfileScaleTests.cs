using System.Linq;
using System.Numerics;
using Content.Client.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class HumanoidProfileScaleTests
{
    [Test]
    public async Task DefaultClothedPreviewsMatchPlayableSpeciesScaleAfterCustomHeightIsReset()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var appearance = entities.System<HumanoidAppearanceSystem>();
            var inventory = entities.System<InventorySystem>();
            foreach (var species in prototypes.EnumeratePrototypes<SpeciesPrototype>().Where(species => species.RoundStart))
            {
                var playable = prototypes.Index(species.Prototype);
                Assert.That(playable.TryGetComponent<SpriteComponent>(out var authored), Is.True, species.ID);
                var uid = entities.SpawnEntity(species.DollPrototype, MapCoordinates.Nullspace);
                var uniform = entities.SpawnEntity("ClothingUniformJumpsuitEngineering", MapCoordinates.Nullspace);
                try
                {
                    var profile = HumanoidCharacterProfile.DefaultWithSpecies(species.ID);
                    appearance.LoadProfile(uid, profile);
                    var sprite = entities.GetComponent<SpriteComponent>(uid);
                    Assert.That(sprite.Scale, Is.EqualTo(authored!.Scale), species.ID + "/default");
                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True, species.ID);
                    var garmentScale = sprite[sprite.LayerMapGet("jumpsuit-0")].Scale;
                    profile.Appearance.Width = .9f;
                    profile.Appearance.Height = 1.1f;
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite.Scale, Is.EqualTo(new Vector2(.9f, 1.1f)), species.ID + "/custom");
                    Assert.That(sprite[sprite.LayerMapGet("jumpsuit-0")].Scale, Is.EqualTo(garmentScale), species.ID + "/custom garment");
                    profile.Appearance.Width = profile.Appearance.Height = 1;
                    appearance.LoadProfile(uid, profile);
                    Assert.That(sprite.Scale, Is.EqualTo(authored.Scale), species.ID + "/reset");
                    Assert.That(sprite[sprite.LayerMapGet("jumpsuit-0")].Scale, Is.EqualTo(garmentScale), species.ID + "/reset garment");
                }
                finally
                {
                    entities.DeleteEntity(uid);
                    entities.DeleteEntity(uniform);
                }
            }
        });
        await pair.CleanReturnAsync();
    }
}
