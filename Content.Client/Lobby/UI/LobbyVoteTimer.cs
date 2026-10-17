using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>A centered readout whose changing text never imposes a minimum card width.</summary>
public sealed class LobbyVoteTimer : Control
{
    private readonly Label _time;
    private readonly Font _captionFont = LobbyConsoleTheme.GetFont(9);
    private readonly Font _timeFont = LobbyConsoleTheme.GetFont();
    private readonly string _caption = Loc.GetString("deeplagoon-lobby-vote-time-remaining");

    public LobbyVoteTimer(Label time)
    {
        _time = time;
        _time.Visible = false;
        AddChild(time);
        MouseFilter = MouseFilterMode.Ignore;
        RectClipContent = true;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
        => new(0, Math.Max(16, _timeFont.GetHeight(UIScale) / UIScale));

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var text = _time.Text ?? "";
        var captionSize = handle.GetDimensions(_captionFont, _caption, UIScale);
        var timeSize = handle.GetDimensions(_timeFont, text, UIScale);
        var left = (PixelWidth - captionSize.X - timeSize.X - 9 * UIScale) / 2;
        handle.DrawString(_captionFont, new Vector2(left, (PixelHeight - captionSize.Y) / 2), _caption, UIScale,
            LobbyConsoleTheme.Green.WithAlpha(0.55f));
        handle.DrawString(_timeFont, new Vector2(left + captionSize.X + 9 * UIScale, (PixelHeight - timeSize.Y) / 2),
            text, UIScale, LobbyConsoleTheme.Green);
    }
}
