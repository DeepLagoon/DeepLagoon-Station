using System.Globalization;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client.Lobby.UI;

/// <summary>A seven-segment readout that preserves the full signed currency balance.</summary>
public sealed class LobbySegmentCounter : Control
{
    private const float DigitAdvance = 20;
    private const float GroupSpace = 8;
    private const float DigitHeight = 26;
    private static readonly byte[] Patterns = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
    private long? _value;
    private string _text = "-";

    public long? Value
    {
        get => _value;
        set
        {
            _value = value;
            _text = value?.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ') ?? "-";
            ToolTip = value?.ToString(CultureInfo.InvariantCulture) ?? Loc.GetString("deeplagoon-lobby-counter-loading");
            InvalidateMeasure();
        }
    }

    public string DisplayText => _text;

    public LobbySegmentCounter()
    {
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Pass;
        VerticalAlignment = VAlignment.Center;
    }

    private float NaturalWidth
    {
        get
        {
            var width = 0f;
            foreach (var digit in _text)
                width += digit == ' ' ? GroupSpace : DigitAdvance;
            return Math.Max(1, width);
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize) => new(Math.Min(NaturalWidth, availableSize.X), DigitHeight + 2);

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var scale = Math.Min(UIScale, PixelWidth / NaturalWidth);
        if (scale <= 0)
            return;
        var origin = new Vector2((PixelWidth - NaturalWidth * scale) / 2, (PixelHeight - DigitHeight * scale) / 2);
        foreach (var digit in _text)
        {
            if (digit != ' ')
            {
                var pattern = digit == '-' ? 0x40 : Patterns[digit - '0'];
                // Broad strokes and a clear gap between digits stay legible beneath the CRT lines.
                DrawSegment(handle, origin, scale, 0, 0, 16, 4, (pattern & 1) != 0);
                DrawSegment(handle, origin, scale, 12, 0, 4, 13, (pattern & 2) != 0);
                DrawSegment(handle, origin, scale, 12, 13, 4, 13, (pattern & 4) != 0);
                DrawSegment(handle, origin, scale, 0, 22, 16, 4, (pattern & 8) != 0);
                DrawSegment(handle, origin, scale, 0, 13, 4, 13, (pattern & 16) != 0);
                DrawSegment(handle, origin, scale, 0, 0, 4, 13, (pattern & 32) != 0);
                DrawSegment(handle, origin, scale, 0, 11, 16, 4, (pattern & 64) != 0);
            }
            origin.X += (digit == ' ' ? GroupSpace : DigitAdvance) * scale;
        }
    }

    private void DrawSegment(DrawingHandleScreen handle, Vector2 origin, float scale, float x, float y,
        float width, float height, bool active)
    {
        // Unlit segments made the small readout look like a mesh of overlapping eights.
        if (!active)
            return;

        // Solid square ends keep adjacent lit strokes connected at this small size.
        var topLeft = origin + new Vector2(x, y) * scale;
        var bottomRight = origin + new Vector2(x + width, y + height) * scale;
        handle.DrawRect(new UIBox2(MathF.Round(topLeft.X), MathF.Round(topLeft.Y),
            MathF.Round(bottomRight.X), MathF.Round(bottomRight.Y)), LobbyConsoleTheme.Hover);
    }
}
