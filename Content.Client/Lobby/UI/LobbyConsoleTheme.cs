using System.Numerics;
using System.Text;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Content.Client.Resources;
using Robust.Client.UserInterface;

namespace Content.Client.Lobby.UI;

/// <summary>The approved console-template settings, shared by lobby screens and controls.</summary>
public static class LobbyConsoleTheme
{
    public const float SidebarWidth = 400;
    public const float RowHeight = 42;
    public const float BlockGap = 12;
    public const float ScreenInset = 30;
    public const float ContentInset = 42;
    public const float ScanlineOpacity = 0.12f;
    public const float ScanlinePitch = 5;
    public static readonly Color Green = Color.FromHex("#82EE99");
    public static readonly Color Screen = Color.FromHex("#0B1810");
    public static readonly Color Hover = Color.FromHex("#CCFFD4");
    public static readonly Color Metal = Color.FromHex("#9AA4A1");

    public static Font GetFont(int size = 13) => new ConsoleFont(IoCManager.Resolve<IResourceCache>()
        .GetFont("/Fonts/RobotoMono/RobotoMono-Regular.ttf", size), size);

    public static StyleBoxFlat CreateHeadingStyle() => new()
    {
        BackgroundColor = Color.FromHex("#14271B"),
        BorderColor = Green.WithAlpha(0.3f),
        BorderThickness = new Thickness(0, 0, 0, 1),
        ContentMarginLeftOverride = 8,
        ContentMarginRightOverride = 8,
        ContentMarginTopOverride = 4,
        ContentMarginBottomOverride = 4,
    };

    // The template uses CSS pixels; VectorFont sizes are points at 96 DPI.
    // Apply 72/96 while keeping the user's UI/font DPI scaling in effect.
    private sealed class ConsoleFont(Font font, int pixels) : Font
    {
        private const float PixelScale = 0.75f;
        public override int GetAscent(float scale) => font.GetAscent(scale * PixelScale);
        public override int GetHeight(float scale) => font.GetHeight(scale * PixelScale);
        public override int GetDescent(float scale) => font.GetDescent(scale * PixelScale);
        public override int GetLineHeight(float scale) => (int) MathF.Round(pixels * scale);
        public override CharMetrics? GetCharMetrics(Rune rune, float scale, bool fallback = true)
            => font.GetCharMetrics(rune, scale * PixelScale, fallback);
        public override float DrawChar(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale,
            Color color, bool fallback = true) => font.DrawChar(handle, rune, baseline, scale * PixelScale, color, fallback);
        public override float DrawCharOutline(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale,
            TextOutline outline, bool fallback = true) => font.DrawCharOutline(handle, rune, baseline, scale * PixelScale, outline, fallback);
    }
}
