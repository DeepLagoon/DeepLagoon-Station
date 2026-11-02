using System.Linq;
using Content.Shared.Armor;
using Content.Server.IdentityManagement;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Item;
using Content.Shared.Foldable;
using Robust.Shared.Serialization.Manager;
using Content.Shared.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.Clothing.Systems;

public sealed partial class ChameleonClothingSystem : SharedChameleonClothingSystem
{
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private ISerializationManager _visualSerialization = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ChameleonClothingComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ChameleonClothingComponent, ChameleonPrototypeSelectedMessage>(OnSelected);
    }

    private void OnMapInit(EntityUid uid, ChameleonClothingComponent component, MapInitEvent args)
    {
        SetSelectedPrototype(uid, component.Default, true, component);
    }

    private void OnSelected(EntityUid uid, ChameleonClothingComponent component, ChameleonPrototypeSelectedMessage args)
    {
        SetSelectedPrototype(uid, args.SelectedId, component: component);
    }

    private void UpdateUi(EntityUid uid, ChameleonClothingComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var state = new ChameleonBoundUserInterfaceState(component.Slot, component.Default, component.RequireTag);
        UI.SetUiState(uid, ChameleonUiKey.Key, state);
    }

    /// <summary>
    ///     Change chameleon items name, description and sprite to mimic other entity prototype.
    /// </summary>
    public void SetSelectedPrototype(EntityUid uid, string? protoId, bool forceUpdate = false,
        ChameleonClothingComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        // check that wasn't already selected
        // forceUpdate on component init ignores this check
        if (component.Default == protoId && component.CopiedAppearance == null && !component.Folded && !forceUpdate)
            return;

        // make sure that it is valid change
        if (string.IsNullOrEmpty(protoId) || !_proto.TryIndex(protoId, out EntityPrototype? proto))
            return;
        if (!IsValidTarget(proto, component.Slot, component.RequireTag))
            return;
        component.Default = protoId;
        component.CopiedAppearance = null;
        component.Folded = proto.TryComp<FoldableComponent>(out var fold, Factory) && fold.IsFolded;

        UpdateIdentityBlocker(uid, component, proto);
        UpdateVisuals(uid, component);
        UpdateUi(uid, component);
        Dirty(uid, component);
    }

    /// <summary>Capture before unequipping, since unequipping may reset visual state.</summary>
    public ChameleonClothingAppearance? CaptureAppearance(EntityUid source)
    {
        if (!TryComp<ClothingComponent>(source, out var clothing) ||
            !TryComp<ItemComponent>(source, out var item) || MetaData(source).EntityPrototype is not { } prototype)
            return null;
        EntProtoId id = TryComp<ChameleonClothingComponent>(source, out var chameleon) && chameleon.Default is { } disguise
            ? disguise : prototype.ID;
        var fit = clothing.BodyFit;
        if (fit == ClothingBodyFit.Auto && TryComp<ArmorComponent>(source, out var armor) &&
            (armor.Modifiers.Coefficients.Any(p => p.Key.Id is "Blunt" or "Slash" or "Piercing" && p.Value < .8f) ||
             armor.Modifiers.FlatReduction.Any(p => p.Key.Id is "Blunt" or "Slash" or "Piercing" && p.Value > 0)))
            fit = ClothingBodyFit.Rigid;
        return new ChameleonClothingAppearance
        {
            Prototype = id,
            PaletteColor = TryComp<Content.Shared._DeepLagoon.LustWish.LustWishPaletteComponent>(source, out var palette)
                ? palette.Color : null,
            RandomSprites = TryComp<Content.Shared.Sprite.RandomSpriteComponent>(source, out var random)
                ? _visualSerialization.CreateCopy(random.Selected, notNullableOverride: true) : new(),
            Folded = chameleon?.Folded ?? (TryComp<FoldableComponent>(source, out var folded) && folded.IsFolded),
            ClothingRsi = clothing.RsiPath,
            EquippedPrefix = clothing.EquippedPrefix,
            EquippedState = clothing.EquippedState,
            ClothingVisuals = _visualSerialization.CreateCopy(clothing.ClothingVisuals, notNullableOverride: true),
            BodyFit = (byte) fit,
            FaceFit = clothing.FaceFit,
            HairOcclusion = clothing.HairOcclusion,
            PreserveLegHem = clothing.PreserveLegHem,
            HidesUniform = clothing.HidesUniform,
            MappedLayer = clothing.MappedLayer,
            ItemRsi = item.RsiPath,
            HeldPrefix = item.HeldPrefix,
            InhandVisuals = _visualSerialization.CreateCopy(item.InhandVisuals, notNullableOverride: true),
        };
    }

    /// <summary>Trusted server path for equipment the visitor actually wore, including non-uplink disguises.</summary>
    public void CopyAppearance(EntityUid uid, ChameleonClothingAppearance appearance)
    {
        if (!TryComp<ChameleonClothingComponent>(uid, out var component) ||
            !_proto.TryIndex(appearance.Prototype, out var prototype))
            return;
        component.Default = appearance.Prototype;
        component.CopiedAppearance = _visualSerialization.CreateCopy(appearance, notNullableOverride: true);
        component.Folded = appearance.Folded;
        UpdateIdentityBlocker(uid, component, prototype);
        UpdateVisuals(uid, component);
        UpdateUi(uid, component);
        Dirty(uid, component);
    }
    private void UpdateIdentityBlocker(EntityUid uid, ChameleonClothingComponent component, EntityPrototype proto)
    {
        if (proto.HasComponent<IdentityBlockerComponent>(Factory))
            EnsureComp<IdentityBlockerComponent>(uid);
        else
            RemComp<IdentityBlockerComponent>(uid);

        if (component.User != null)
            _identity.QueueIdentityUpdate(component.User.Value);
    }
}
