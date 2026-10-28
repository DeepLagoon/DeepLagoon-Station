using Content.Shared.Humanoid;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private void RefreshAppearanceAccess()
    {
        if (Profile == null) return;
        Profile = Profile.WithCharacterAppearance(HumanoidCharacterAppearance.EnsureValid(
            Profile.Appearance, Profile.Species, Profile.Sex, Profile.UnrestrictedMarkings));
        _catalogKey = _selectionCatalogKey = null;
        UpdateMarkings();
        UpdateHairPickers();
        UpdateCMarkingsHair();
        UpdateCMarkingsFacialHair();
        ReloadProfilePreview();
    }
}
