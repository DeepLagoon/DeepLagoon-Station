using Content.Shared.Humanoid;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool SetPlantigradeLegs(bool enabled)
    {
        if (Profile == null || _prototypeManager.Index(Profile.Species).PlantigradeSprites == null) return false;
        var appearance = new HumanoidCharacterAppearance(Profile.Appearance) { PlantigradeLegs = enabled };
        Profile = Profile.WithCharacterAppearance(appearance);
        UpdateLegShapeControls();
        SetDirty();
        ReloadProfilePreview();
        return true;
    }

    private void UpdateLegShapeControls()
    {
        PlantigradeLegs.Visible = Profile != null && _prototypeManager.Index(Profile.Species).PlantigradeSprites != null;
        PlantigradeLegs.Pressed = Profile?.Appearance.PlantigradeLegs ?? false;
    }
}
