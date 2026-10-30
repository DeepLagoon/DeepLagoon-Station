using Content.Client.Clothing;
using Content.Client.Inventory;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpaceSuitUniformVisualTests
{
    [Test]
    public async Task OuterClothingHidesAndRestoresEveryUniformLayer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            // Reuse one server/client startup for all suit families and the ordinary-coat control.
            foreach (var (outerPrototype, hidesUniform) in new[]
            {
                ("ClothingOuterHardsuitEngineering", true),
                ("ClothingOuterHardsuitEVA", true),
                ("ClothingOuterSuitEmergency", true),
                ("ClothingModsuitChestplateStandard", true),
                ("ClothingOuterHardsuitM86Mk4", true),
                ("ClothingOuterHardsuitUIEnforcerMKII", true),
                ("ClothingOuterSuitSpaceNinja", true),
                ("ClothingOuterHardsuitCarp", true),
                ("ClothingOuterCoatLab", false),
            })
            {
                var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
                var uniform = entities.SpawnEntity("ClothingUniformJumpskirtColorRed", MapCoordinates.Nullspace);
                var outer = entities.SpawnEntity(outerPrototype, MapCoordinates.Nullspace);
                try
                {
                    var inventory = entities.System<InventorySystem>();
                    var clothing = entities.System<ClientClothingSystem>();
                    var slots = entities.GetComponent<InventorySlotsComponent>(uid);
                    var sprite = entities.GetComponent<SpriteComponent>(uid);
                    void CheckUniform(bool visible)
                    {
                        Assert.That(slots.VisualLayerKeys["jumpsuit"].Count, visible ? Is.GreaterThan(0) : Is.Zero, outerPrototype);
                        Assert.That(sprite.LayerMapTryGet("jumpsuit-0", out _), Is.EqualTo(visible), outerPrototype);
                        Assert.That(inventory.TryGetSlotEntity(uid, "jumpsuit", out var equipped), Is.True);
                        Assert.That(equipped, Is.EqualTo(uniform), "Hiding visuals must keep the uniform equipped.");
                    }

                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                    CheckUniform(true);
                    Assert.That(inventory.TryEquip(uid, outer, "outerClothing", force: true), Is.True);
                    CheckUniform(!hidesUniform);
                    clothing.InitClothing(uid, entities.GetComponent<InventoryComponent>(uid));
                    CheckUniform(!hidesUniform);
                    Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true, reparent: false), Is.True);
                    CheckUniform(true);

                    // Also cover equipping and replacing the uniform after the suit is already worn.
                    Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                    Assert.That(inventory.TryEquip(uid, outer, "outerClothing", force: true), Is.True);
                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                    CheckUniform(!hidesUniform);
                    Assert.That(inventory.TryUnequip(uid, "jumpsuit", force: true, reparent: false), Is.True);
                    Assert.That(inventory.TryEquip(uid, uniform, "jumpsuit", force: true), Is.True);
                    CheckUniform(!hidesUniform);
                    Assert.That(inventory.TryUnequip(uid, "outerClothing", force: true, reparent: false), Is.True);
                    CheckUniform(true);
                }
                finally
                {
                    entities.DeleteEntity(uid);
                    entities.DeleteEntity(uniform);
                    entities.DeleteEntity(outer);
                }
            }
        });
        await pair.CleanReturnAsync();
    }
}
