using Content.Shared.Chat.TypingIndicator;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Clothing;

namespace Content.Client.Chat.TypingIndicator;

public sealed partial class TypingIndicatorVisualizerSystem : VisualizerSystem<TypingIndicatorComponent>
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TypingIndicatorComponent, ClothingDidEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<TypingIndicatorComponent, ClothingDidUnequippedEvent>(OnUnequipped);
    }

    private void OnEquipped(EntityUid uid, TypingIndicatorComponent component, ref ClothingDidEquippedEvent args) => Refresh(uid);

    private void OnUnequipped(EntityUid uid, TypingIndicatorComponent component, ref ClothingDidUnequippedEvent args) => Refresh(uid);

    private void Refresh(EntityUid uid)
    {
        if (TryComp<AppearanceComponent>(uid, out var appearance))
            AppearanceSystem.QueueUpdate(uid, appearance);
    }

    protected override void OnAppearanceChange(EntityUid uid, TypingIndicatorComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        AppearanceSystem.TryGetData<TypingIndicatorState>(uid, TypingIndicatorVisuals.State, out var state);
        var layer = args.Sprite.LayerMapReserveBlank(TypingIndicatorLayers.Base);
        args.Sprite.LayerSetVisible(layer, state.IsIdle() || state.IsTyping());
        if (!state.IsIdle() && !state.IsTyping())
        {
            SpriteSystem.LayerSetAutoAnimated((uid, args.Sprite), layer, false);
            return;
        }

        var currentTypingIndicator = component.TypingIndicatorPrototype;

        var evt = new BeforeShowTypingIndicatorEvent();

        if (TryComp<InventoryComponent>(uid, out var inventoryComp))
            _inventory.RelayEvent((uid, inventoryComp), ref evt);

        var overrideIndicator = evt.GetMostRecentIndicator();

        if (overrideIndicator != null)
            currentTypingIndicator = overrideIndicator.Value;

        currentTypingIndicator = state switch
        {
            TypingIndicatorState.EmoteIdle or TypingIndicatorState.EmoteTyping => "DeepLagoonEmote",
            TypingIndicatorState.LoocIdle or TypingIndicatorState.LoocTyping => "DeepLagoonLooc",
            _ => currentTypingIndicator,
        };

        if (!_prototypeManager.TryIndex(currentTypingIndicator, out var proto))
        {
            args.Sprite.LayerSetVisible(layer, false);
            Log.Error($"Unknown typing indicator id: {currentTypingIndicator}");
            return;
        }

        SpriteSystem.LayerSetRsi((uid, args.Sprite), layer, proto.SpritePath,
            state.IsTyping() ? proto.TypingState : proto.IdleState);
        args.Sprite.LayerSetShader(layer, proto.Shader);
        args.Sprite.LayerSetOffset(layer, proto.Offset);
        args.Sprite.LayerSetColor(layer, proto.Color);
        SpriteSystem.LayerSetAutoAnimated((uid, args.Sprite), layer, state.IsTyping());
        if (state.IsIdle())
            SpriteSystem.LayerSetAnimationTime((uid, args.Sprite), layer, 0);
    }
}
