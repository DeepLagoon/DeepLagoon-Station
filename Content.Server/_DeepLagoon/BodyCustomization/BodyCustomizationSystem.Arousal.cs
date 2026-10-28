using Content.Shared.Humanoid;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed partial class BodyCustomizationSystem
{
    [Dependency] private IGameTiming _timing = default!;

    private const float GenitalArousalThreshold = 0.2f;
    private static readonly TimeSpan GenitalArousalCooldown = TimeSpan.FromSeconds(30);

    public void UpdateGenitalArousal(EntityUid uid, float current, float maximum)
    {
        if (!float.IsFinite(current) || !float.IsFinite(maximum) || maximum <= 0
            || !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)) return;
        var threshold = maximum * GenitalArousalThreshold;
        if (current == threshold) return;
        var aroused = current > threshold;
        if (humanoid.GenitalAroused == aroused
            || TryComp<GenitalArousalCooldownComponent>(uid, out var cooldown)
            && _timing.CurTime < cooldown.NextAutomaticChange) return;
        SetGenitalArousal(uid, aroused);
    }
}
