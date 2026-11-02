using System.Collections.Generic;
using System.Linq;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Foldable;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Client.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using ServerChameleon = Content.Server.Clothing.Systems.ChameleonClothingSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentChameleonAppearanceTests
{
    [Test]
    public async Task EntryCopiesWornAppearanceAndVariantsToClientAndManualSelectionResetsSnapshot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default, uniform = default, hat = default, coat = default;
        NetEntity netBody = default, netUniform = default, netHat = default, netCoat = default;
        var originals = new Dictionary<string, EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var session = pair.Server.PlayerMan.Sessions.Single();
            var inventory = e.System<InventorySystem>();
            var chameleon = e.System<ServerChameleon>();
            body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            foreach (var (slot, prototype) in new[]
            {
                ("jumpsuit", "ClothingUniformJumpsuitColorBlue"), ("shoes", "ClothingShoesColorBlue"),
                ("gloves", "ClothingHandsGlovesColorYellow"), ("ears", "ClothingHeadsetCargo"),
                ("id", "CaptainPDA"), ("back", "ClothingBackpack"),
                ("head", "ClothingHeadHatBluesoft"), ("outerClothing", "ClothingOuterCoatLab"),
            })
            {
                var original = e.SpawnEntity(prototype, map.GridCoords);
                originals[slot] = original;
                if (e.TryGetComponent<FoldableComponent>(original, out var fold))
                    e.System<FoldableSystem>().SetFolded(original, fold, true);
                Assert.That(inventory.TryEquip(body, original, slot, force: true), Is.True, slot);
            }
            var palette = e.EnsureComponent<Content.Shared._DeepLagoon.LustWish.LustWishPaletteComponent>(originals["jumpsuit"]);
            palette.Color = Color.Aqua;
            e.Dirty(originals["jumpsuit"], palette);
            Assert.That(e.System<ApartmentSystem>().Enter(session, session.UserId, out var message), Is.True, message);
            foreach (var slot in new[] { "jumpsuit", "shoes", "gloves", "ears", "id", "back" })
            {
                Assert.That(inventory.TryGetSlotEntity(body, slot, out var item), Is.True, slot);
                var disguise = e.GetComponent<ChameleonClothingComponent>(item.Value);
                Assert.That(disguise.Default?.Id, Is.EqualTo(e.GetComponent<MetaDataComponent>(originals[slot]).EntityPrototype.ID));
                Assert.That(disguise.CopiedAppearance, Is.Not.Null, slot);
            }
            inventory.TryGetSlotEntity(body, "back", out var bag);
            var storage = e.GetComponent<StorageComponent>(bag.Value).Container;
            hat = storage.ContainedEntities.Single(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype.ID == "ClothingHeadHatChameleon");
            coat = storage.ContainedEntities.Single(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype.ID == "ClothingOuterChameleon");
            foreach (var (item, slot) in new[] { (hat, "head"), (coat, "outerClothing") })
            {
                Assert.That(e.System<SharedContainerSystem>().Remove(item, storage), Is.True);
                Assert.That(inventory.TryEquip(body, item, slot, force: true), Is.True);
            }
            inventory.TryGetSlotEntity(body, "jumpsuit", out var wornUniform);
            uniform = wornUniform.Value;
            netBody = e.GetNetEntity(body);
            netUniform = e.GetNetEntity(uniform);
            netHat = e.GetNetEntity(hat);
            netCoat = e.GetNetEntity(coat);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var uniformEntity = e.GetEntity(netUniform);
            var cloth = e.GetComponent<ClothingComponent>(uniformEntity);
            Assert.That(e.GetComponent<ChameleonClothingComponent>(uniformEntity).CopiedAppearance.PaletteColor, Is.EqualTo(Color.Aqua));
            Assert.That(e.GetComponent<SpriteComponent>(uniformEntity).Color, Is.EqualTo(Color.Aqua));
            Assert.That(e.GetComponent<ClothingComponent>(e.GetEntity(netHat)).EquippedPrefix, Is.EqualTo("flipped"));
            Assert.That(e.GetComponent<ClothingComponent>(e.GetEntity(netCoat)).EquippedPrefix, Is.EqualTo("open"));
            var wearer = e.GetComponent<SpriteComponent>(e.GetEntity(netBody));
            Assert.That(wearer[wearer.LayerMapGet("jumpsuit-0")].Color, Is.EqualTo(Color.Aqua));
            Assert.That(wearer[wearer.LayerMapGet("head-0")].RsiState.Name, Is.EqualTo("flipped-equipped-HELMET"));
            Assert.That(wearer[wearer.LayerMapGet("outerClothing-0")].RsiState.Name, Is.EqualTo("open-equipped-OUTERCLOTHING"));
            foreach (var item in new[] { netHat, netCoat })
            {
                var sprite = e.GetComponent<SpriteComponent>(e.GetEntity(item));
                Assert.That(sprite[sprite.LayerMapGet("foldedLayer")].Visible, Is.True);
                Assert.That(sprite[sprite.LayerMapGet("unfoldedLayer")].Visible, Is.False);
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var chameleon = e.System<ServerChameleon>();
            Assert.That(chameleon.ToggleClothingVariant((hat, e.GetComponent<ChameleonClothingComponent>(hat))), Is.True);
            Assert.That(chameleon.ToggleClothingVariant((coat, e.GetComponent<ChameleonClothingComponent>(coat))), Is.True);
            Assert.That(chameleon.ToggleClothingVariant((uniform, e.GetComponent<ChameleonClothingComponent>(uniform))), Is.False);
            Assert.That(e.GetComponent<ChameleonClothingComponent>(hat).Folded, Is.False);
            Assert.That(e.GetComponent<ClothingComponent>(hat).EquippedPrefix, Is.Null);
            chameleon.SetSelectedPrototype(uniform, "ClothingUniformJumpsuitColorRed");
            Assert.That(e.GetComponent<ChameleonClothingComponent>(uniform).CopiedAppearance, Is.Null);
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var wearer = e.GetComponent<SpriteComponent>(e.GetEntity(netBody));
            Assert.That(e.GetComponent<ChameleonClothingComponent>(e.GetEntity(netHat)).Folded, Is.False);
            Assert.That(e.GetComponent<ClothingComponent>(e.GetEntity(netHat)).EquippedPrefix, Is.Null);
            Assert.That(e.GetComponent<ClothingComponent>(e.GetEntity(netHat)).EquippedState, Is.Null);
            var hatSprite = e.GetComponent<SpriteComponent>(e.GetEntity(netHat));
            Assert.That(hatSprite[hatSprite.LayerMapGet("foldedLayer")].Visible, Is.False);
            Assert.That(wearer[wearer.LayerMapGet("head-0")].RsiState.Name, Is.EqualTo("equipped-HELMET"));
            Assert.That(wearer[wearer.LayerMapGet("outerClothing-0")].RsiState.Name, Is.EqualTo("equipped-OUTERCLOTHING"));
            Assert.That(wearer[wearer.LayerMapGet("jumpsuit-0")].Color, Is.Not.EqualTo(Color.Aqua));
            foreach (var item in new[] { netHat, netCoat })
            {
                var sprite = e.GetComponent<SpriteComponent>(e.GetEntity(item));
                Assert.That(sprite[sprite.LayerMapGet("foldedLayer")].Visible, Is.False);
                Assert.That(sprite[sprite.LayerMapGet("unfoldedLayer")].Visible, Is.True);
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(e.System<ApartmentSystem>().Leave(session.UserId, out var message), Is.True, message);
            foreach (var (slot, original) in originals)
            {
                e.System<InventorySystem>().TryGetSlotEntity(body, slot, out var restored);
                Assert.That(restored, Is.EqualTo(original));
            }
            Assert.That(e.GetComponent<FoldableComponent>(originals["head"]).IsFolded, Is.True);
            Assert.That(e.GetComponent<FoldableComponent>(originals["outerClothing"]).IsFolded, Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task EntryPreservesRandomOutfitLayersAndPaletteOnClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default, source = default, uniform = default;
        NetEntity netBody = default, netUniform = default;
        Dictionary<string, (string State, Color? Color)> selected = new();
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var session = pair.Server.PlayerMan.Sessions.Single();
            body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            source = e.SpawnEntity("ClothingUniformRandomPunkCroptop", map.GridCoords);
            selected = new(e.GetComponent<Content.Shared.Sprite.RandomSpriteComponent>(source).Selected);
            Assert.That(selected, Is.Not.Empty);
            Assert.That(e.System<InventorySystem>().TryEquip(body, source, "jumpsuit", force: true), Is.True);
            Assert.That(e.System<ApartmentSystem>().Enter(session, session.UserId, out var message), Is.True, message);
            e.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var item);
            uniform = item.Value;
            netBody = e.GetNetEntity(body);
            netUniform = e.GetNetEntity(uniform);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var e = pair.Client.EntMan;
            var item = e.GetEntity(netUniform);
            var clothing = e.GetComponent<ClothingComponent>(item);
            var world = e.GetComponent<SpriteComponent>(item);
            var wearer = e.GetComponent<SpriteComponent>(e.GetEntity(netBody));
            foreach (var (key, variant) in selected)
            {
                var layer = clothing.ClothingVisuals["jumpsuit"].Single(layer => layer.MapKeys?.Contains(key) == true);
                Assert.That(layer.State, Is.EqualTo(variant.State), key);
                Assert.That(layer.Color, Is.EqualTo(variant.Color), key);
                Assert.That(world[world.LayerMapGet(key)].RsiState.Name, Is.EqualTo(variant.State), key);
                Assert.That(world[world.LayerMapGet(key)].Color, Is.EqualTo(variant.Color ?? Color.White), key);
                Assert.That(wearer[wearer.LayerMapGet(key)].RsiState.Name, Is.EqualTo(variant.State), key);
                Assert.That(wearer[wearer.LayerMapGet(key)].Color, Is.EqualTo(variant.Color ?? Color.White), key);
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var session = pair.Server.PlayerMan.Sessions.Single();
            Assert.That(e.System<ApartmentSystem>().Leave(session.UserId, out var message), Is.True, message);
            Assert.That(e.GetComponent<Content.Shared.Sprite.RandomSpriteComponent>(source).Selected, Is.EqualTo(selected));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(body);
        });
        await pair.CleanReturnAsync();
    }}
