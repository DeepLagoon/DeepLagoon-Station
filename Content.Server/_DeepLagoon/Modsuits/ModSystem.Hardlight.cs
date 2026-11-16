using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._DeepLagoon.Radial;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    [Dependency] private IPrototypeManager _hardlightPrototypes = default!;

    private ModHardlightParts PresentHardlightParts(EntityUid user) =>
        TryComp<HumanoidAppearanceComponent>(user, out var humanoid)
            ? ModHardlightRegions.Present(humanoid, _hardlightPrototypes)
            : ModHardlightParts.None;

    private void InitializeHardlight()
    {
        SubscribeLocalEvent<ModControlComponent, ModHardlightStyleMessage>(OnHardlightStyleMessage);
    }

    private void AddHardlightChoices(Entity<ModControlComponent> suit, EntityUid user, List<RadialChoice> choices)
    {
        var present = PresentHardlightParts(user);
        foreach (var (part, id, _, state) in ModHardlightRegions.Options)
        {
            if ((present & part) == 0)
                continue;

            SpriteSpecifier sprite = new SpriteSpecifier.Rsi(new ResPath("_DeepLagoon/Modsuits/actions.rsi"), state);
            if (part == ModHardlightParts.Hair && HairIcon(user) is {} hair)
                sprite = hair;
            choices.Add(new((int) part, Loc.GetString($"mod-hardlight-part-{id}"), sprite,
                (suit.Comp.HardlightParts & part) != 0));
        }
    }

    private SpriteSpecifier? HairIcon(EntityUid user)
    {
        if (!TryComp<HumanoidAppearanceComponent>(user, out var humanoid) ||
            !humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Hair, out var markings))
            return null;

        foreach (var marking in markings)
        {
            if (marking.Visible && _hardlightPrototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype) &&
                prototype.Sprites.Count > 0)
                return prototype.Sprites[0];
        }

        return null;
    }

    private void SelectHardlight(Entity<ModControlComponent> suit, EntityUid user, int choice)
    {
        var present = PresentHardlightParts(user);
        if (!Array.Exists(ModHardlightRegions.Options, option => (int) option.Part == choice && (present & option.Part) != 0))
            return;

        suit.Comp.HardlightParts ^= (ModHardlightParts) choice;
        RefreshHardlightEditor(suit);
        RefreshHardlight(suit, user);
        RefreshSuitActionIcons(suit);
    }

    private void ToggleAllHardlight(Entity<ModControlComponent> suit, EntityUid user)
    {
        var present = PresentHardlightParts(user);
        suit.Comp.HardlightParts = (suit.Comp.HardlightParts & present) == ModHardlightParts.None
            ? present
            : ModHardlightParts.None;
        RefreshHardlightEditor(suit);
        RefreshHardlight(suit, user);
        RefreshSuitActionIcons(suit);
    }

    private void OpenHardlightEditor(Entity<ModControlComponent> suit, EntityUid user)
    {
        if (!HasComp<HumanoidAppearanceComponent>(user))
            return;

        _ui.TryOpenUi(suit.Owner, ModHardlightKey.Key, user);
        RefreshHardlightEditor(suit);
    }

    private void RefreshHardlightEditor(Entity<ModControlComponent> suit)
    {
        if (_ui.IsUiOpen(suit.Owner, ModHardlightKey.Key))
            _ui.SetUiState(suit.Owner, ModHardlightKey.Key,
                new ModHardlightState(suit.Comp.HardlightColor, suit.Comp.HardlightEffect, suit.Comp.HardlightParts & ModHardlightParts.All));
    }

    private void OnHardlightStyleMessage(Entity<ModControlComponent> suit, ref ModHardlightStyleMessage args)
    {
        if (!Equals(args.UiKey, ModHardlightKey.Key) || !IsWorn(suit, args.Actor) ||
            !_blocker.CanInteract(args.Actor, suit) || !Enum.IsDefined(args.Effect) ||
            args.Color is not { Length: 7 } || args.Color[0] != '#' || !Color.TryFromHex(args.Color, out var color))
            return;

        suit.Comp.HardlightColor = color;
        suit.Comp.HardlightEffect = args.Effect;
        RefreshHardlight(suit, args.Actor);
        RefreshHardlightEditor(suit);
    }

    private void RefreshHardlight(Entity<ModControlComponent> suit, EntityUid user)
    {
        var visible = ModHardlightParts.None;
        if ((suit.Comp.HardlightParts & ModHardlightParts.All) != 0 && IsSealed(suit) &&
            TryComp<ToggleableClothingComponent>(suit, out var deployment))
        {
            var enabled = suit.Comp.HardlightParts & PresentHardlightParts(user);
            foreach (var (part, _, slot, _) in ModHardlightRegions.Options)
            {
                if ((enabled & part) == 0 ||
                    !_inventory.TryGetSlotEntity(user, slot, out var equipment) ||
                    !ToggleableClothingSystem.TryGetAttachedSlot(deployment, equipment.Value, out _) ||
                    !TryComp<Content.Shared._Goobstation.Clothing.Components.SealableClothingComponent>(equipment, out var seal) ||
                    !seal.IsSealed)
                    continue;

                visible |= part;
            }
        }

        if (visible == ModHardlightParts.None)
        {
            RemComp<ModHardlightVisualComponent>(user);
            return;
        }

        var visual = EnsureComp<ModHardlightVisualComponent>(user);
        if (visual.Parts == visible && visual.Color == suit.Comp.HardlightColor && visual.Effect == suit.Comp.HardlightEffect)
            return;

        visual.Parts = visible;
        visual.Color = suit.Comp.HardlightColor;
        visual.Effect = suit.Comp.HardlightEffect;
        Dirty(user, visual);
    }
}
