namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private bool _updatingBloodColor;

    private void InitializeBloodColorControls()
    {
        CustomBloodColor.OnToggled += args =>
        {
            if (!_updatingBloodColor)
                SetBloodColorOverride(args.Pressed);
        };
        BloodColorPicker.OnEyeColorPicked += color =>
        {
            if (_updatingBloodColor || Profile == null || !CustomBloodColor.Pressed)
                return;
            Profile = Profile.WithBloodColor(color.ToHexNoAlpha());
            ReloadProfilePreview();
        };
    }

    private bool SetBloodColorOverride(bool enabled)
    {
        if (Profile == null)
            return false;
        Profile = Profile.WithBloodColor(enabled ? (Profile.BloodColor.Length == 0 ? "#800000" : Profile.BloodColor) : "");
        UpdateBloodColorControls();
        ReloadProfilePreview();
        return true;
    }

    private void UpdateBloodColorControls()
    {
        _updatingBloodColor = true;
        CustomBloodColor.Disabled = Profile == null;
        CustomBloodColor.Pressed = !string.IsNullOrEmpty(Profile?.BloodColor);
        BloodColorPicker.Visible = CustomBloodColor.Pressed;
        if (Profile != null)
            BloodColorPicker.SetData(Color.TryFromHex(Profile.BloodColor, out var color) ? color : Color.FromHex("#800000"));
        _updatingBloodColor = false;
    }
}
