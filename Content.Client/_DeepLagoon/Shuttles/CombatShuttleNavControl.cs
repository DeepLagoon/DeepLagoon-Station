using System.Numerics;
using Content.Client._Mono.FireControl.UI;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Shuttles;

/// <summary>One radar for piloting and aiming, with a lightweight CRT overlay.</summary>
public sealed class CombatShuttleNavControl : FireControlNavControl
{
    private float _sweep;
    private float _noiseTime;
    public void StopFiring() => _isMouseDown = false;

    public CombatShuttleNavControl()
    {
        RectClipContent = true;
        DefaultCursorShape = Robust.Client.UserInterface.Control.CursorShape.Crosshair;
    }

    protected override void VisibilityChanged(bool newVisible)
    {
        base.VisibilityChanged(newVisible);
        if (!VisibleInTree)
            _isMouseDown = false;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        if (!VisibleInTree)
        {
            _isMouseDown = false;
            return;
        }
        // Releasing the mouse outside the viewport must also stop a held burst.
        if (_isMouseDown && !IoCManager.Resolve<IInputManager>().IsKeyDown(Keyboard.Key.MouseLeft))
            _isMouseDown = false;
        base.FrameUpdate(args);
        _sweep = (_sweep + args.DeltaSeconds / 5f) % 1f;
        _noiseTime += args.DeltaSeconds;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (PixelSize.X <= 0 || PixelSize.Y <= 0)
            return;
        handle.UseShader(null);

        // Fixed upper bound on draw work; no noise textures or off-screen render targets.
        var step = MathF.Max(4f, PixelSize.Y / 96f);
        for (var y = 0f; y < PixelSize.Y; y += step)
            handle.DrawLine(new Vector2(0, y), new Vector2(PixelSize.X, y), new Color(0, 0.025f, 0.01f, 0.06f));

        var scanY = _sweep * PixelSize.Y;
        handle.DrawRect(new UIBox2(0, scanY, PixelSize.X, MathF.Min(PixelSize.Y, scanY + 3)), new Color(0.2f, 0.6f, 0.8f, 0.025f));
        // Sparse deterministic phosphor noise, refreshed at 12 Hz. No allocations,
        // textures, random-number service or full-screen postprocessing.
        var seed = (uint)(_noiseTime * 12) + 1;
        for (var i = 0; i < 48; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            var x = (seed & 65535) / 65535f * MathF.Max(0, PixelSize.X - 2);
            seed = seed * 1664525u + 1013904223u;
            var y = (seed & 65535) / 65535f * MathF.Max(0, PixelSize.Y - 1);
            handle.DrawRect(new UIBox2(x, y, x + 2, y + 1), new Color(0.5f, 0.8f, 1f, 0.04f));
        }
        for (var edge = 0; edge < 4; edge++)
        {
            var inset = edge * 4f;
            if (inset * 2 >= MathF.Min(PixelSize.X, PixelSize.Y))
                break;
            handle.DrawRect(new UIBox2(inset, inset, PixelSize.X - inset, PixelSize.Y - inset),
                new Color(0, 0.03f, 0.015f, 0.16f), filled: false);
        }
    }
}
