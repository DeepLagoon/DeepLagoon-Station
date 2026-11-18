using System.Collections.Generic;
using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;

namespace Content.Shared._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelCoverageSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<InteractionPanelCoverageComponent, GotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<InteractionPanelCoverageComponent, GotUnequippedEvent>(OnUnequipped);
    }

    private void OnEquipped(EntityUid uid, InteractionPanelCoverageComponent component, GotEquippedEvent args)
        => RaiseLocalEvent(args.Equipee, new InteractionPanelCoverageChangedEvent());

    private void OnUnequipped(EntityUid uid, InteractionPanelCoverageComponent component, GotUnequippedEvent args)
        => RaiseLocalEvent(args.Equipee, new InteractionPanelCoverageChangedEvent());

    public bool IsCovered(EntityUid uid, string? part)
    {
        if (part == null || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        part = Normalize(part);
        if (IsExposed(humanoid, part)) return false;
        if (!TryComp<InventoryComponent>(uid, out var inventory)) return false;
        var slots = _inventory.GetSlotEnumerator((uid, inventory));
        while (slots.NextItem(out var item, out _))
            if (TryComp<InteractionPanelCoverageComponent>(item, out var coverage) && coverage.Covers.Contains(part))
                return true;
        return false;
    }

    private static bool IsExposed(HumanoidAppearanceComponent humanoid, string part) =>
        humanoid.BodyFeatures.Any(f => f.Part == part && f.Enabled && f.Visibility == 2 && f.ShowThroughClothing);

    public static string Normalize(string part) => part switch
    {
        "urethra" => "penis",
        "nipples" => "breasts",
        _ => part,
    };

    public HashSet<string> CoveredParts(EntityUid uid)
    {
        var covered = new HashSet<string>();
        if (!TryComp<InventoryComponent>(uid, out var inventory)) return covered;
        var slots = _inventory.GetSlotEnumerator((uid, inventory));
        while (slots.NextItem(out var item, out _))
            if (TryComp<InteractionPanelCoverageComponent>(item, out var coverage))
                foreach (var part in coverage.Covers) covered.Add(part);
        return covered;
    }
}

public readonly record struct InteractionPanelCoverageChangedEvent;
