using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Humanoid;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    private void InitializeHardlight()
    {
        SubscribeLocalEvent<ModControlComponent, ModHardlightStyleMessage>(OnHardlightStyleMessage);
    }

    private void AddHardlightChoices(Entity<ModControlComponent> suit, EntityUid user, List<ModRadialChoice> choices)
    {
        if (!HasComp<HumanoidAppearanceComponent>(user))
            return;

        foreach (var (part, id, _, state) in ModHardlightRegions.Options)
        {
            var sprite = state is "helmet" or "chestplate" or "gauntlets" or "boots"
                ? new SpriteSpecifier.Rsi(new ResPath("_DeepLagoon/Clothing/Modsuits/standard.rsi"), state)
                : new SpriteSpecifier.Rsi(new ResPath("_DeepLagoon/Modsuits/actions.rsi"), state);
            choices.Add(new((int) part, Loc.GetString($"mod-hardlight-part-{id}"), sprite,
                (suit.Comp.HardlightParts & part) != 0));
        }
    }

    private void SelectHardlight(Entity<ModControlComponent> suit, EntityUid user, int choice)
    {
        if (!HasComp<HumanoidAppearanceComponent>(user) ||
            !Array.Exists(ModHardlightRegions.Options, option => (int) option.Part == choice))
            return;

        suit.Comp.HardlightParts ^= (ModHardlightParts) choice;
        RefreshHardlightEditor(suit);
        RefreshHardlight(suit, user);
        RefreshSuitActionIcons(suit);
    }

    private void ToggleAllHardlight(Entity<ModControlComponent> suit, EntityUid user)
    {
        if (!HasComp<HumanoidAppearanceComponent>(user))
            return;

        suit.Comp.HardlightParts = suit.Comp.HardlightParts == ModHardlightParts.None
            ? ModHardlightParts.All
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
                new ModHardlightState(suit.Comp.HardlightColor, suit.Comp.HardlightEffect, suit.Comp.HardlightParts));
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
        if (IsSealed(suit) && HasComp<HumanoidAppearanceComponent>(user) &&
            TryComp<ToggleableClothingComponent>(suit, out var deployment))
        {
            foreach (var (part, _, slot, _) in ModHardlightRegions.Options)
            {
                if ((suit.Comp.HardlightParts & part) == 0 ||
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
