using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction.Events;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem
{
    private EntityUid? Protection(EntityUid uid, string? part)
    {
        if (part == null || !TryComp<LustWishWornEffectsComponent>(uid, out var worn) || !worn.Items.TryGetValue(part, out var item)) return null;
        return TryGetEntity(item, out var entity) && HasComp<BlueMoonProtectionComponent>(entity) ? entity : null;
    }

    public bool IsChastened(EntityUid uid, string? part) => Protection(uid, part) is { } item && Comp<BlueMoonProtectionComponent>(item).Chastity;

    private bool CaptureProtectionFluid(EntityUid uid, string part, Solution fluid)
    {
        if (Protection(uid, part) is not { } item || !Comp<BlueMoonProtectionComponent>(item).CaptureFluids) return false;
        if (_solutions.TryGetSolution(item, "captured", out var solution, out var contents))
            _solutions.TryAddSolution(solution.Value, fluid.SplitSolution(FixedPoint2.Min(fluid.Volume, contents.AvailableVolume)));
        Spill(uid, fluid);
        return true;
    }

    private void OnUnwrapProtection(EntityUid uid, BlueMoonProtectionComponent component, UseInHandEvent args)
    {
        if (args.Handled || !component.Wrapped) return;
        component.Wrapped = false;
        args.Handled = true;
        UpdateProtectionVisual(uid, component);
    }

    private void UpdateProtectionVisual(EntityUid uid, BlueMoonProtectionComponent component)
    {
        var volume = _solutions.TryGetSolution(uid, "captured", out _, out var contents) ? (float)contents.Volume : 0;
        EntityManager.System<SharedAppearanceSystem>().SetData(uid, BlueMoonProtectionVisuals.State,
            component.Wrapped ? "wrapped" : volume >= 250 ? "huge" : volume > 100 ? "large" : volume >= 50 ? "medium" : volume > 0 ? "filled" : "empty");
    }
}
