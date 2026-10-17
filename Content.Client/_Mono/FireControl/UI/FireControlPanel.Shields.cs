using Content.Shared.Exodus.ShipShields;
using Robust.Client.Graphics;

namespace Content.Client._Mono.FireControl.UI;

public sealed partial class FireControlPanel
{
    private readonly StyleBoxFlat _shieldHealthStyle = new();

    private void UpdateShieldState(ShipShieldState? state)
    {
        ShieldContainer.Visible = state.HasValue;
        if (state is not { } shield)
            return;
        var status = !shield.Powered ? "ship-shield-no-power"
            : shield.OverloadAccumulator > 0 ? "ship-shield-overload"
            : shield.Recharging && shield.HealthFraction < 1f ? "ship-shield-recharging"
            : shield.Active ? "ship-shield-active" : "ship-shield-offline";
        ShieldStatusLabel.Text = Loc.GetString(status, ("seconds", Math.Ceiling(shield.OverloadAccumulator)));
        var health = Math.Clamp(shield.HealthFraction, 0f, 1f);
        ShieldHealthLabel.Text = Loc.GetString("ship-shield-health", ("percent", Math.Round(health * 100f)));
        ShieldHealthBar.Value = health;
        _shieldHealthStyle.BackgroundColor = health <= 0.25f ? Color.Red : health <= 0.5f ? Color.Orange : Color.LimeGreen;
        ShieldHealthBar.ForegroundStyleBoxOverride = _shieldHealthStyle;
        ShieldLoadLabel.Text = Loc.GetString("ship-shield-load",
            ("draw", Math.Round((shield.BaseDraw + shield.Draw) / 1000f, 1)),
            ("maximum", Math.Round((shield.BaseDraw + shield.MaxDraw) / 1000f, 1)));
    }
}
