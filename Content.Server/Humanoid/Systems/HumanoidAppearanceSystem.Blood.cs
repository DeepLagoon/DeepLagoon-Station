using Content.Server.Body.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;

namespace Content.Server.Humanoid;

public sealed partial class HumanoidAppearanceSystem
{
    [Dependency] private BloodstreamSystem _bloodstream = default!;

    public override void LoadProfile(EntityUid uid, HumanoidCharacterProfile? profile, HumanoidAppearanceComponent? humanoid = null)
    {
        base.LoadProfile(uid, profile, humanoid);
        if (profile != null)
            _bloodstream.SetBloodColor(uid, Color.TryFromHex(profile.BloodColor, out var color) ? color : null);
    }
}
