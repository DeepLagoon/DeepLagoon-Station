using System.Linq;
using Content.Client.PDA;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client.Clothing.Systems;

// All valid items for chameleon are calculated on client startup and stored in dictionary.
public sealed partial class ChameleonClothingSystem : SharedChameleonClothingSystem
{
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private Content.Client.Sprite.RandomSpriteSystem _randomSprite = default!;
    [Dependency] private Content.Client.Clothing.ClientClothingSystem _clothingVisuals = default!;
    [Dependency] private Content.Shared.Item.SharedItemSystem _itemVisuals = default!;
    private readonly HashSet<EntityUid> _pendingClothingRefresh = new();

    private static readonly SlotFlags[] IgnoredSlots =
    {
        SlotFlags.All,
        SlotFlags.PREVENTEQUIP,
        SlotFlags.NONE
    };
    private static readonly SlotFlags[] Slots = Enum.GetValues<SlotFlags>().Except(IgnoredSlots).ToArray();

    private readonly Dictionary<SlotFlags, List<string>> _data = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ChameleonClothingComponent, AfterAutoHandleStateEvent>(HandleState);

        PrepareAllVariants();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnProtoReloaded);
    }

    protected override void AfterUpdateVisuals(EntityUid uid, ChameleonClothingComponent component)
    {
        base.AfterUpdateVisuals(uid, component);
        _pendingClothingRefresh.Add(uid);
        if (component.CopiedAppearance is { RandomSprites.Count: > 0 } copy)
            _randomSprite.ApplySelectedAppearance(uid, copy.RandomSprites);
        if (component.CopiedAppearance?.PaletteColor is { } color && TryComp<SpriteComponent>(uid, out var sprite))
            sprite.Color = color;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pendingClothingRefresh.Count == 0)
            return;
        // Rebuild after the complete game state, including containers and inventory, has been applied.
        var wearers = new HashSet<EntityUid>();
        foreach (var item in _pendingClothingRefresh)
        {
            if (!Exists(item))
                continue;
            _itemVisuals.VisualsChanged(item);
            var wearer = Transform(item).ParentUid;
            if (HasComp<InventoryComponent>(wearer))
                wearers.Add(wearer);
        }
        _pendingClothingRefresh.Clear();
        foreach (var wearer in wearers)
            _clothingVisuals.InitClothing(wearer, Comp<InventoryComponent>(wearer));
    }

    private void OnProtoReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<EntityPrototype>())
            PrepareAllVariants();
    }

    private void HandleState(EntityUid uid, ChameleonClothingComponent component, ref AfterAutoHandleStateEvent args)
    {
        UpdateVisuals(uid, component);
    }

    protected override void UpdateSprite(EntityUid uid, EntityPrototype proto)
    {
        base.UpdateSprite(uid, proto);
        if (TryComp(uid, out SpriteComponent? sprite)
            && proto.TryComp(out SpriteComponent? otherSprite, Factory))
        {
            sprite.CopyFrom(otherSprite);
            // Foldable disguises use the same layers as the original hat, jacket, etc.
            if (TryComp<ChameleonClothingComponent>(uid, out var chameleon))
            {
                if (sprite.LayerMapTryGet("foldedLayer", out var foldedLayer))
                    sprite.LayerSetVisible(foldedLayer, chameleon.Folded);
                if (sprite.LayerMapTryGet("unfoldedLayer", out var unfoldedLayer))
                    sprite.LayerSetVisible(unfoldedLayer, !chameleon.Folded);
            }
        }

        // Edgecase for PDAs to include visuals when UI is open
        if (TryComp(uid, out PdaBorderColorComponent? borderColor)
            && proto.TryComp(out PdaBorderColorComponent? otherBorderColor, Factory))
        {
            borderColor.BorderColor = otherBorderColor.BorderColor;
            borderColor.AccentHColor = otherBorderColor.AccentHColor;
            borderColor.AccentVColor = otherBorderColor.AccentVColor;
        }
    }

    /// <summary>
    ///     Get a list of valid chameleon targets for these slots.
    /// </summary>
    public IEnumerable<string> GetValidTargets(SlotFlags slot)
    {
        var set = new HashSet<string>();
        foreach (var availableSlot in _data.Keys)
        {
            if (slot.HasFlag(availableSlot))
            {
                set.UnionWith(_data[availableSlot]);
            }
        }
        return set;
    }

    private void PrepareAllVariants()
    {
        _data.Clear();
        var prototypes = _proto.EnumeratePrototypes<EntityPrototype>();

        foreach (var proto in prototypes)
        {
            // check if this is valid clothing
            if (!IsValidTarget(proto))
                continue;
            if (!proto.TryComp(out ClothingComponent? item, Factory))
                continue;

            // sort item by their slot flags
            // one item can be placed in several buckets
            foreach (var slot in Slots)
            {
                if (!item.Slots.HasFlag(slot))
                    continue;

                if (!_data.ContainsKey(slot))
                {
                    _data.Add(slot, new List<string>());
                }
                _data[slot].Add(proto.ID);
            }
        }
    }
}
