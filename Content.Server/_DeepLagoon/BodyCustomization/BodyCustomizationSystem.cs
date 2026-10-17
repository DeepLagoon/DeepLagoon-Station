using System.Linq;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.FixedPoint;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed class BodyCustomizationSystem : EntitySystem
{
    public const int UnitsPerStep = 5;
    private static readonly int DosePerStep = FixedPoint2.New(UnitsPerStep).Value;

    public bool ApplyDose(EntityUid uid, string part, int direction, FixedPoint2 units)
    {
        if (!BodyCustomizationCatalog.CanResize(part) || direction is not (-1 or 1)
            || units <= 0
            || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        var features = BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures);
        if (!features.Any(f => f.Part == part && f.Enabled)) return false;
        var progress = EnsureComp<BodySizeDoseComponent>(uid);
        var dose = progress.Doses.GetValueOrDefault(part) + (long) units.Value * direction;
        var changed = false;
        while (Math.Abs(dose) >= DosePerStep)
        {
            var step = Math.Sign(dose);
            if (!BodyCustomizationCatalog.Resize(features, part, step))
            {
                // Do not bank excess doses at a size boundary or loop over huge quantities.
                dose %= DosePerStep;
                break;
            }
            changed = true;
            dose -= DosePerStep * step;
        }
        progress.Doses[part] = dose;
        if (!changed) return false;
        humanoid.BodyFeatures = features;
        Changed(uid, humanoid);
        return true;
    }

    public bool SetVisibility(EntityUid uid, string part, int visibility)
    {
        if (visibility is < 0 or > 2 || part == "womb"
            || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return false;
        var features = BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures);
        var feature = features.FirstOrDefault(f => f.Part == part && f.Enabled);
        if (feature == null) return false;
        feature.Visibility = visibility;
        humanoid.BodyFeatures = features;
        Changed(uid, humanoid);
        return true;
    }

    private void Changed(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        Dirty(uid, humanoid);
        var ev = new BodyCustomizationChangedEvent();
        RaiseLocalEvent(uid, ref ev);
    }
}
