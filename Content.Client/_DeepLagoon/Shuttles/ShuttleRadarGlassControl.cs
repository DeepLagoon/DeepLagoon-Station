using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Shuttles;

/// <summary>
/// Circular aperture and glass above the native radar, including its child controls.
/// One analytic shader pass, with no framebuffer copies or off-screen render targets.
/// </summary>
public sealed class ShuttleRadarGlassControl : Control
{
    private readonly ShaderInstance _glass;
    private readonly Font _plateFont;
    private readonly Texture _instruments;
    private readonly Texture _shell;
    public bool ShowHardware { get; set; } = true;
    public Vector2 PanelTextureOffset { get; set; }

    public ShuttleRadarGlassControl()
    {
        MouseFilter = MouseFilterMode.Stop;
        RectClipContent = true;
        _glass = IoCManager.Resolve<IPrototypeManager>()
            .Index<ShaderPrototype>("ShuttleRadarGlass").InstanceUnique();
        var resources = IoCManager.Resolve<IResourceCache>();
        _plateFont = new VectorFont(resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 9);
        _instruments = resources.GetResource<TextureResource>("/Web/DeepLagoon/ship-console-instruments.png").Texture;
        _shell = resources.GetResource<TextureResource>("/Web/DeepLagoon/ship-console-shell.png").Texture;
    }

    // Pass input through the glass; intercept the hidden corners, including dock buttons.
    protected override bool HasPoint(Vector2 point)
    {
        var radius = MathF.Max(0, MathF.Min(Size.X, Size.Y) / 2 - 4);
        return base.HasPoint(point) && Vector2.DistanceSquared(point, Size / 2) >= radius * radius;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (PixelSize.X <= 0 || PixelSize.Y <= 0)
            return;

        _glass.SetParameter("viewport_size", (Vector2)PixelSize);
        _glass.SetParameter("rim_width", 4 * UIScale);
        _glass.SetParameter("panel_scale", UIScale);
        _glass.SetParameter("panel_offset", PanelTextureOffset);
        _glass.SetParameter("panel_texture", ShowHardware ? 1f : 0f);
        handle.UseShader(_glass);
        handle.DrawTextureRect(Texture.White, PixelSizeBox);
        handle.UseShader(null);
        DrawCornerHardware(handle);
    }

    private void DrawCornerHardware(DrawingHandleScreen handle)
    {
        var size = MathF.Min(PixelSize.X, PixelSize.Y);
        // Hide decoration first on small windows; never consume radar space.
        if (!ShowHardware || size < 330 * UIScale) return;
        var inset = size * 0.02f;
        // Source regions have a fixed aspect ratio. All three fit in the hidden
        // corners, outside the radar aperture, at every supported scale.
        var plate = new UIBox2(inset, inset, inset + size * .20f, inset + size * .20f / 6.5f);
        handle.DrawTextureRectRegion(_shell, plate, new UIBox2(1105, 19, 1385, 62));
        const string label = "DL / RADAR";
        var fontScale = MathF.Min(UIScale, size / 520f);
        var measured = handle.GetDimensions(_plateFont, label, fontScale);
        fontScale *= MathF.Min(1, MathF.Min((plate.Width - 8 * UIScale) / MathF.Max(1, measured.X),
            (plate.Height - 2 * UIScale) / MathF.Max(1, _plateFont.GetHeight(fontScale))));
        measured = handle.GetDimensions(_plateFont, label, fontScale);
        // DrawString adds the ascent itself: its argument is a top-left origin,
        // not a baseline. Adding the ascent here moved the label off the plate.
        var origin = new Vector2(plate.Left + (plate.Width - measured.X) / 2,
            plate.Top + (plate.Height - _plateFont.GetHeight(fontScale)) / 2);
        handle.DrawString(_plateFont, origin + new Vector2(0, UIScale), label, fontScale, Color.Black);
        handle.DrawString(_plateFont, origin, label, fontScale, Color.FromHex("#d8d7ca"));
        var ribbonWidth = size * .14f;
        var harnessWidth = size * .09f;
        handle.DrawTextureRectRegion(_instruments,
            new UIBox2(inset, size - inset - ribbonWidth * 123 / 164, inset + ribbonWidth, size - inset),
            new UIBox2(140, 730, 304, 853));
        var harness = new UIBox2(size - inset - harnessWidth, size - inset - harnessWidth * 114 / 86, size - inset, size - inset);
        // A recessed service opening, with a metal lip and contact shadow,
        // ties the cable artwork to the surrounding panel.
        handle.DrawRect(new UIBox2(harness.Left - UIScale, harness.Top, harness.Right + UIScale, harness.Bottom + 3 * UIScale), Color.FromHex("#080d11"));
        handle.DrawRect(new UIBox2(harness.Left - UIScale, harness.Top - UIScale, harness.Right + UIScale, harness.Bottom + UIScale), Color.FromHex("#596166"));
        handle.DrawTextureRectRegion(_instruments, harness, new UIBox2(809, 750, 895, 864));
    }

    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _glass.Dispose();
        base.Dispose(disposing);
    }
}
