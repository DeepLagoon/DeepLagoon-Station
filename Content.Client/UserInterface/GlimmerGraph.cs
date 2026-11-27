using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client.UserInterface;

public sealed class GlimmerGraph : Control
{
    private readonly List<int> _glimmer;
    private const int XOffset = 15;
    private const int YOffset = 210;
    private const int Length = 450;
    private static int YOffsetTop => YOffset - 200;

    private const int GridSpacing = 30;

    private static readonly Color GridColor = Color.FromHex("#686868");
    private static readonly Color BorderColor = Color.FromHex("#8f8f92");
    private static readonly Color LineColor = Color.FromHex("#A200BB");

    public GlimmerGraph(List<int> glimmer)
    {
        _glimmer = glimmer;
        HorizontalAlignment = HAlignment.Left;
        VerticalAlignment = VAlignment.Bottom;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var box = new UIBox2(new Vector2(XOffset, YOffset), new Vector2(XOffset + Length, YOffsetTop));
        handle.DrawRect(box, Color.FromHex("#424245"));

        DrawGrid(handle, box);

        if (_glimmer.Count < 2)
            return;

        var spacing = Length / (_glimmer.Count - 1);

        var i = 0;
        while (i + 1 < _glimmer.Count)
        {
            var vector1 = new Vector2(XOffset + i * spacing, YOffset - _glimmer[i] / 5);
            var vector2 = new Vector2(XOffset + (i + 1) * spacing, YOffset - _glimmer[i + 1] / 5);
            handle.DrawLine(vector1, vector2, LineColor);
            handle.DrawLine(vector1 + new Vector2(0, 1), vector2 + new Vector2(0, 1), LineColor);
            handle.DrawLine(vector1 - new Vector2(0, 1), vector2 - new Vector2(0, 1), LineColor);
            i++;
        }

        for (var x = XOffset; x <= XOffset + Length; x += GridSpacing)
            handle.DrawLine(new Vector2(x, YOffset), new Vector2(x, YOffsetTop), GridColor);
    }

    private static void DrawGrid(DrawingHandleScreen handle, UIBox2 box)
    {
        for (var y = YOffset; y >= YOffsetTop; y -= GridSpacing)
            handle.DrawLine(new Vector2(XOffset, y), new Vector2(XOffset + Length, y), GridColor);

        handle.DrawRect(box, BorderColor, filled: false);
    }
}

