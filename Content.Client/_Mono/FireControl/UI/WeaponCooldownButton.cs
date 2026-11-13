using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._Mono.FireControl.UI;

internal sealed partial class WeaponCooldownButton : Button
{
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private TimeSpan _start;
    private TimeSpan _ready;

    public void UpdateCooldown(TimeSpan ready)
    {
        if (ready > _ready)
            _start = _timing.CurTime;

        _ready = ready;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (!Pressed)
            return;

        var duration = (_ready - _start).TotalSeconds;
        var progress = duration <= 0 ? 1f : (float) Math.Clamp((_timing.CurTime - _start).TotalSeconds / duration, 0, 1);
        var height = Math.Min(3f * UIScale, PixelHeight);
        handle.DrawRect(new UIBox2(0, PixelHeight - height, PixelWidth * progress, PixelHeight), Color.SkyBlue);
    }
}
