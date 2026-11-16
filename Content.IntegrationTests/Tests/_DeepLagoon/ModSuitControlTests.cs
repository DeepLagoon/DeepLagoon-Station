using System;
using System.Linq;
using Content.Server._DeepLagoon.Modsuits;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._DeepLagoon.Radial;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared.Actions;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Inventory;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Shared.Prototypes;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ModSuitControlTests
{
    [Test]
    public async Task HardlightChoicesFollowTheCharacterAppearance()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            var wearer = em.SpawnEntity("MobMoth", map.GridCoords);
            var appearance = em.GetComponent<HumanoidAppearanceComponent>(wearer);
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            Assert.That(ModHardlightRegions.Present(appearance, prototypes),
                Is.EqualTo(ModHardlightParts.Wings | ModHardlightParts.Antennae));

            appearance.MarkingSet.RemoveCategory(MarkingCategories.HeadTop);
            em.System<SharedHumanoidAppearanceSystem>().AddMarking(wearer, "MothAntennasNone", Color.White, forced: true);
            var chest = BodyCustomizationCatalog.Default("breasts");
            chest.Enabled = true;
            appearance.BodyFeatures.Add(chest);
            var buttocks = BodyCustomizationCatalog.Default("butt");
            buttocks.Enabled = true;
            buttocks.Visibility = 0;
            appearance.BodyFeatures.Add(buttocks);

            Assert.That(ModHardlightRegions.Present(appearance, prototypes),
                Is.EqualTo(ModHardlightParts.Wings | ModHardlightParts.Breasts));

            buttocks.Visibility = 1;
            Assert.That(ModHardlightRegions.Present(appearance, prototypes),
                Is.EqualTo(ModHardlightParts.Wings | ModHardlightParts.Breasts | ModHardlightParts.Buttocks));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SelectPartThenActivateAndStyleHardlight()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        var inventory = em.System<InventorySystem>();
        var ui = em.System<SharedUserInterfaceSystem>();
        EntityUid wearer = default, suit = default, helmet = default, chest = default, hardHat = default;
        NetEntity netWearer = default, netSuit = default;
        var color = Color.FromHex("#FF128A");
        const string hair = "HumanHairFloorlengthBedhead";
        const string hairLayer = "HumanHairFloorlengthBedhead-floorlength_bedhead";
        const string projection = "mod-hardlight/marking/" + hairLayer;

        await pair.Server.WaitAssertion(() =>
        {
            wearer = em.SpawnEntity("MobHuman", map.GridCoords);
            suit = em.SpawnEntity("ClothingModsuitStandartPowerCell", map.GridCoords);
            em.System<SharedHumanoidAppearanceSystem>().AddMarking(wearer, hair, Color.White, forced: true);
            netWearer = em.GetNetEntity(wearer);
            netSuit = em.GetNetEntity(suit);
            em.System<SharedGodmodeSystem>().EnableGodmode(wearer);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), wearer);
            Assert.That(inventory.TryEquip(wearer, suit, "back", silent: true, force: true), Is.True);
            hardHat = em.SpawnEntity("ClothingHeadHatHardhatYellow", map.GridCoords);
            Assert.That(inventory.TryEquip(wearer, hardHat, "head", silent: true, force: true), Is.True);
            var clothes = em.GetComponent<ToggleableClothingComponent>(suit).ClothingUids;
            helmet = clothes.Single(part => part.Value == "head").Key;
            chest = clothes.Single(part => part.Value == "outerClothing").Key;
            Assert.That(em.GetComponent<ActionsComponent>(wearer).Actions.Select(a => em.GetComponent<MetaDataComponent>(a).EntityPrototype?.ID),
                Does.Contain("ActionModDeploy").And.Contain("ActionModActivate").And.Contain("ActionModHardlight"));

            SendAction(ModActionKind.Deploy);
            Assert.That(ui.IsUiOpen(suit, ModRadialKey.Key, wearer), Is.True);
            Assert.That(ui.TryGetUiState<AnchoredRadialState>(suit, ModRadialKey.Key, out var radial), Is.True);
            Assert.That(radial!.Anchor, Is.EqualTo(netWearer));
            Assert.That(radial.Viewer, Is.EqualTo(netWearer));
            Assert.That(em.GetComponent<HumanoidAppearanceComponent>(wearer).HiddenLayers, Does.Not.ContainKey(HumanoidVisualLayers.Hair));
            Select(em.GetNetEntity(helmet).Id);
            Assert.That(Equipped("head", helmet), Is.True);
            Assert.That(em.GetComponent<HumanoidAppearanceComponent>(wearer).HiddenLayers, Does.ContainKey(HumanoidVisualLayers.Hair));
            Assert.That(Equipped("outerClothing", chest), Is.False);
            SendAction(ModActionKind.Hardlight);
            Assert.That(ui.TryGetUiState<AnchoredRadialState>(suit, ModRadialKey.Key, out var hardlight), Is.True);
            Assert.That(hardlight!.Choices.Select(c => c.Id), Is.EquivalentTo(new[] { (int) ModHardlightParts.Hair }));
            Select((int) ModHardlightParts.Tail);
            Assert.That(em.GetComponent<ModControlComponent>(suit).HardlightParts, Is.EqualTo(ModHardlightParts.None));
            Select((int) ModHardlightParts.Hair);
            SendAction(ModActionKind.HardlightStyle);
            em.EventBus.RaiseLocalEvent(suit, new ModHardlightStyleMessage("#FF128A", ModHardlightEffect.Wave)
                { Actor = wearer, UiKey = ModHardlightKey.Key });
            SendAction(ModActionKind.Activate);
            em.EventBus.RaiseLocalEvent(suit, new OpenModControlEvent { Performer = wearer });
            Assert.That(em.GetComponent<SealableClothingControlComponent>(suit).IsInProcess, Is.True);
            Assert.That(em.HasComponent<ModHardlightVisualComponent>(wearer), Is.False);
        });

        await pair.RunSeconds(2.1f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Equipped("outerClothing", chest), Is.False);
            Assert.That(em.GetComponent<SealableClothingComponent>(helmet).IsSealed, Is.True);
            var visual = em.GetComponent<ModHardlightVisualComponent>(wearer);
            Assert.That(visual.Parts, Is.EqualTo(ModHardlightParts.Hair));
            Assert.That(visual.Color, Is.EqualTo(color));
            Assert.That(visual.Effect, Is.EqualTo(ModHardlightEffect.Wave));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var clientUi = client.System<SharedUserInterfaceSystem>();
            Assert.That(clientUi.TryGetUiState<ModUiState>(client.GetEntity(netSuit), ModUiKey.Key, out var panel), Is.True);
            Assert.That(panel!.Parts.Single(part => part.Slot == "head").Deployed, Is.True);
            Assert.That(panel.Parts.Single(part => part.Slot == "outerClothing").Deployed, Is.False);
            var sprite = client.GetComponent<SpriteComponent>(client.GetEntity(netWearer));
            Assert.That(sprite.LayerMapTryGet(hairLayer, out var hiddenHair), Is.True);
            Assert.That(sprite.LayerData[hiddenHair].Visible, Is.False);
            Assert.That(sprite.LayerMapTryGet(projection, out var projected), Is.True);
            Assert.That(sprite.LayerData[projected].Color, Is.EqualTo(color));
            Assert.That(sprite.LayerData[projected].Shader, Is.Not.Null);
            Assert.That(sprite.LayerMapTryGet("mod-hardlight/body/Chest", out _), Is.False);
        });

        await pair.Server.WaitAssertion(() => SendAction(ModActionKind.Deploy, alternate: true));
        await pair.RunSeconds(1.1f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Equipped("outerClothing", chest), Is.True);
            Assert.That(inventory.TryGetSlotEntity(wearer, "gloves", out _), Is.False);
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(inventory.TryGetSlotEntity(wearer, "gloves", out _), Is.True);
            Assert.That(inventory.TryGetSlotEntity(wearer, "shoes", out _), Is.True);
            SendAction(ModActionKind.Module, alternate: true);
            var flash = em.GetComponent<ModControlComponent>(suit).Modules.ContainedEntities.Single(item =>
                em.GetComponent<ModModuleComponent>(item).Mode == ModModuleMode.Flashlight);
            Select(em.GetNetEntity(flash).Id);
            var module = em.GetComponent<ModModuleComponent>(flash);
            Assert.That(module.PinnedAction, Is.Not.Null);
            em.EventBus.RaiseLocalEvent(flash, new ModPinnedModuleEvent { Performer = wearer, Alternate = true });
            Assert.That(module.PinnedAction, Is.Null);
            SendAction(ModActionKind.Activate);
        });

        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.HasComponent<ModHardlightVisualComponent>(wearer), Is.False);
            Assert.That(Equipped("head", helmet), Is.True);
            Assert.That(em.GetComponent<SealableClothingComponent>(helmet).IsSealed, Is.False);
            Assert.That(Equipped("outerClothing", chest), Is.True);
        });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var sprite = client.GetComponent<SpriteComponent>(client.GetEntity(netWearer));
            Assert.That(sprite.LayerMapTryGet(projection, out _), Is.False);
        });
        await pair.Server.WaitAssertion(() => SendAction(ModActionKind.Activate, alternate: true));
        await pair.RunSeconds(4);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Equipped("head", hardHat), Is.True);
            Assert.That(em.GetComponent<HumanoidAppearanceComponent>(wearer).HiddenLayers, Does.Not.ContainKey(HumanoidVisualLayers.Hair));
            Assert.That(Equipped("outerClothing", chest), Is.False);
        });

        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();

        bool Equipped(string slot, EntityUid part) =>
            inventory.TryGetSlotEntity(wearer, slot, out var equipped) && equipped == part;

        void SendAction(ModActionKind kind, bool alternate = false) =>
            em.EventBus.RaiseLocalEvent(suit, new ModSuitActionEvent { Kind = kind, Alternate = alternate, Performer = wearer });

        void Select(int id) =>
            em.EventBus.RaiseLocalEvent(suit, new RadialSelection(id) { Actor = wearer, UiKey = ModRadialKey.Key });
    }
}
