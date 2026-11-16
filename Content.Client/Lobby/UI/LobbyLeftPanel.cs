using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>Reserves space for the lobby actions before measuring the character preview.</summary>
public sealed class LobbyLeftPanel : Container
{
    private const float PreviewGap = 12;
    private const float MusicGap = 8;
    private Vector2 _measuredSize;
    private float _previewHeight;
    private float _musicHeight;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (ChildCount != 3)
            return Vector2.Zero;

        var preview = GetChild(0);
        var actions = GetChild(1);
        var music = GetChild(2);
        var (width, height) = availableSize;
        _measuredSize = availableSize;
        music.Measure(new Vector2(width, float.PositiveInfinity));
        var musicHeight = music.DesiredSize.Y;

        var actionBudget = Math.Min(height, Math.Max(LobbyButtonPanel.CompactHeight, height - musicHeight - MusicGap));
        actions.Measure(new Vector2(width, actionBudget));
        var afterActions = height - actions.DesiredSize.Y;
        _musicHeight = musicHeight + MusicGap <= afterActions ? musicHeight : 0;
        var musicSpace = _musicHeight > 0 ? _musicHeight + MusicGap : 0;

        preview.Measure(new Vector2(width, Math.Max(0, afterActions - musicSpace - PreviewGap)));
        _previewHeight = preview.DesiredSize.Y;
        return new Vector2(Math.Max(preview.DesiredSize.X, Math.Max(actions.DesiredSize.X, music.DesiredSize.X)),
            actions.DesiredSize.Y + musicSpace + (_previewHeight > 0 ? _previewHeight + PreviewGap : 0));
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        if (ChildCount != 3)
            return finalSize;

        if (_measuredSize != finalSize)
            MeasureOverride(finalSize);

        var actionsY = _previewHeight > 0 ? _previewHeight + PreviewGap : 0;
        GetChild(0).Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(finalSize.X, _previewHeight)));
        GetChild(1).Arrange(UIBox2.FromDimensions(new Vector2(0, actionsY), new Vector2(finalSize.X, GetChild(1).DesiredSize.Y)));
        GetChild(2).Arrange(UIBox2.FromDimensions(new Vector2(0, finalSize.Y - _musicHeight), new Vector2(finalSize.X, _musicHeight)));
        return finalSize;
    }
}
