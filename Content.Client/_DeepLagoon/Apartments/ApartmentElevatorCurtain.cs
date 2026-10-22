using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Apartments;

// Screen-space mist keeps the coordinate change hidden even while destination PVS arrives.
public sealed class ApartmentElevatorCurtain(IGameTiming timing) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;
    private TimeSpan _start, _end;

    public void Show(float duration)
    {
        if (duration <= 0) { _end = TimeSpan.FromSeconds(Math.Min(_end.TotalSeconds, timing.RealTime.TotalSeconds + .4)); return; }
        _start = timing.RealTime;
        _end = _start + TimeSpan.FromSeconds(Math.Clamp(duration, 1, 20));
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var elapsed = (float)(timing.RealTime - _start).TotalSeconds;
        var remaining = (float)(_end - timing.RealTime).TotalSeconds;
        if (remaining <= 0) return;
        var alpha = Math.Clamp(Math.Min(elapsed / .7f, remaining / .7f), 0, 1);
        var size = new Vector2(args.Viewport.Size.X, args.Viewport.Size.Y);
        var handle = args.ScreenHandle;
        handle.DrawRect(new UIBox2(Vector2.Zero, size), new Color(.62f, .67f, .68f, alpha));
        for (var i = 0; i < 16; i++)
        {
            var center = new Vector2((i * 173f + elapsed * (i % 2 == 0 ? 20 : -20)) % (size.X + 200),
                (i * 127f + MathF.Sin(elapsed + i) * 30) % (size.Y + 200));
            handle.DrawCircle(center, 100 + i % 4 * 35, new Color(.8f, .83f, .83f, alpha * .08f));
        }
    }
}
