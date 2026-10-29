using Content.Shared.Chat.TypingIndicator;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class TypingIndicatorVisualTests
{
    [Test]
    public async Task EquippingAndRemovingHeadsetsRefreshesAnIdleBubble()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var appearance = entities.System<AppearanceSystem>();
            var inventory = entities.System<InventorySystem>();
            var uid = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            entities.EnsureComponent<TypingIndicatorComponent>(uid);
            entities.EnsureComponent<AppearanceComponent>(uid);
            var sprite = entities.GetComponent<SpriteComponent>(uid);
            appearance.SetData(uid, TypingIndicatorVisuals.State, TypingIndicatorState.Idle);
            appearance.FrameUpdate(0);
            var layer = sprite.LayerMapGet(TypingIndicatorLayers.Base);
            var defaultState = sprite[layer].RsiState;
            foreach (var (headset, state) in new[] {
                         ("ClothingHeadsetMedical", "medical-idle"),
                         ("ClothingHeadsetEngineering", "engineering-idle"),
                     })
            {
                var item = entities.SpawnEntity(headset, MapCoordinates.Nullspace);
                Assert.That(inventory.TryEquip(uid, item, "ears", silent: true, force: true), Is.True);
                appearance.FrameUpdate(0);
                layer = sprite.LayerMapGet(TypingIndicatorLayers.Base);
                Assert.That(sprite[layer].RsiState.Name, Is.EqualTo(state));
                Assert.That(sprite[layer].AutoAnimated, Is.False);
                Assert.That(inventory.TryUnequip(uid, "ears", out _, silent: true, force: true), Is.True);
                appearance.FrameUpdate(0);
                layer = sprite.LayerMapGet(TypingIndicatorLayers.Base);
                Assert.That(sprite[layer].RsiState, Is.EqualTo(defaultState));
                entities.DeleteEntity(item);
            }
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IdleLayersStopAnimatingAndResumeWithTheCorrectChannel()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var appearance = entities.System<AppearanceSystem>();
            var sprites = entities.System<SpriteSystem>();
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<TypingIndicatorComponent>(uid);
            entities.AddComponent<AppearanceComponent>(uid);
            var sprite = entities.AddComponent<SpriteComponent>(uid);
            foreach (var (typing, idle, state) in new[] {
                         (TypingIndicatorState.Typing, TypingIndicatorState.Idle, "normal"),
                         (TypingIndicatorState.EmoteTyping, TypingIndicatorState.EmoteIdle, "emote"),
                         (TypingIndicatorState.LoocTyping, TypingIndicatorState.LoocIdle, "normal"),
                     })
            {
                appearance.SetData(uid, TypingIndicatorVisuals.State, typing);
                appearance.OnChangeData(uid, sprite);
                var layer = sprite.LayerMapGet(TypingIndicatorLayers.Base);
                Assert.That(sprite[layer].Visible, Is.True);
                Assert.That(sprite[layer].AutoAnimated, Is.True);
                Assert.That(sprite[layer].RsiState.Name, Is.EqualTo(state));
                sprites.LayerSetAnimationTime((uid, sprite), layer, 0.6f);
                appearance.SetData(uid, TypingIndicatorVisuals.State, idle);
                appearance.OnChangeData(uid, sprite);
                Assert.That(sprite[layer].AutoAnimated, Is.False);
                Assert.That(sprite[layer].AnimationFrame, Is.Zero);
                appearance.OnChangeData(uid, sprite);
                Assert.That(sprite[layer].AutoAnimated, Is.False, "Other appearance changes must not restart Idle.");
                appearance.SetData(uid, TypingIndicatorVisuals.State, typing);
                appearance.OnChangeData(uid, sprite);
                Assert.That(sprite[layer].AutoAnimated, Is.True);
            }
            appearance.SetData(uid, TypingIndicatorVisuals.State, TypingIndicatorState.None);
            appearance.OnChangeData(uid, sprite);
            Assert.That(sprite[sprite.LayerMapGet(TypingIndicatorLayers.Base)].Visible, Is.False);
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TypingPrototypesHaveValidSpritesAndHeadsetsInheritTheirDepartmentStyle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var cache = pair.Client.ResolveDependency<IResourceCache>();
            var factory = pair.Client.ResolveDependency<IComponentFactory>();
            var say = prototypes.Index<TypingIndicatorPrototype>("default");
            var looc = prototypes.Index<TypingIndicatorPrototype>("DeepLagoonLooc");
            Assert.That(looc.SpritePath, Is.EqualTo(say.SpritePath));
            Assert.That(looc.TypingState, Is.EqualTo(say.TypingState));
            Assert.That(looc.IdleState, Is.EqualTo(say.IdleState));
            Assert.That(looc.Offset, Is.EqualTo(say.Offset));
            Assert.That(looc.Color, Is.EqualTo(Robust.Shared.Maths.Color.FromHex("#40e0d0")));
            foreach (var prototype in prototypes.EnumeratePrototypes<TypingIndicatorPrototype>())
            {
                var rsi = cache.GetResource<RSIResource>(prototype.SpritePath).RSI;
                Assert.That(rsi.TryGetState(prototype.TypingState, out _), Is.True, prototype.ID);
                Assert.That(rsi.TryGetState(prototype.IdleState, out _), Is.True, prototype.ID);
            }
            foreach (var (headset, style) in new[] {
                         ("ClothingHeadsetMedicalCommon", "DeepLagoonMedical"),
                         ("ClothingHeadsetCMO", "DeepLagoonMedical"),
                         ("ClothingHeadsetCE", "DeepLagoonEngineering"),
                         ("ClothingHeadsetAltEngineeringCommon", "DeepLagoonEngineering"),
                         ("ClothingHeadsetRD", "DeepLagoonScience"),
                         ("ClothingHeadsetQM", "DeepLagoonCargo"),
                         ("ClothingHeadsetCommandCommon", "DeepLagoonCommand"),
                         ("ClothingHeadsetNfsdGreenCommon", "DeepLagoonSecurity"),
                         ("ClothingHeadsetServiceCommon", "DeepLagoonService"),
                     })
            {
                var prototype = prototypes.Index<EntityPrototype>(headset);
                Assert.That(prototype.TryComp<TypingIndicatorClothingComponent>(out var indicator, factory), Is.True, headset);
                Assert.That(indicator!.TypingIndicatorPrototype.Id, Is.EqualTo(style), headset);
                Assert.That(indicator.Priority, Is.EqualTo(10), headset);
            }
        });
        await pair.CleanReturnAsync();
    }
}
