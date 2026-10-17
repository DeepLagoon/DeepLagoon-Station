using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client.Lobby.UI;

/// <summary>Client-local lobby player using the same console housing as the sidebar.</summary>
public sealed class LobbyMusicPlayer : LobbyConsolePanel
{
    public readonly Button Previous = new() { Text = "<", HorizontalExpand = true };
    public readonly Button Next = new() { Text = ">", HorizontalExpand = true };
    public readonly Button Pause = new() { HorizontalExpand = true };
    private readonly Label _heading = new() { Text = Loc.GetString("deeplagoon-lobby-music-heading"), ClipText = true };
    private readonly Label _title = new() { ClipText = true };
    private readonly Label _artist = new() { ClipText = true };
    private readonly CoverDisplay _cover = new();
    private readonly BoxContainer _buttons;

    public bool HasCover => _cover.Texture != null;
    public string? TrackTitle => _title.Text;

    public LobbyMusicPlayer()
    {
        RectClipContent = true;
        PanelOverride = CreateFrame(top: 36, bottom: 36);
        var details = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 6 };
        foreach (var label in new[] { _heading, _title, _artist })
        {
            label.FontOverride = LobbyConsoleTheme.GetFont(label == _title ? 15 : 12);
            label.FontColorOverride = LobbyConsoleTheme.Green;
            label.OutlineThicknessOverride = 0;
            details.AddChild(label);
        }
        Previous.ToolTip = Loc.GetString("deeplagoon-lobby-music-previous");
        Next.ToolTip = Loc.GetString("deeplagoon-lobby-music-next");
        _buttons = new BoxContainer { SeparationOverride = 8 };
        foreach (var button in new[] { Previous, Pause, Next })
        {
            LobbyButtonArtwork.AttachUtility(button, nativeText: true, preserveTooltip: true);
            button.Label.Align = Label.AlignMode.Center;
            button.Label.FontOverride = LobbyConsoleTheme.GetFont(button == Pause ? 13 : 18);
            _buttons.AddChild(button);
        }
        Previous.HorizontalExpand = Next.HorizontalExpand = false;
        Previous.SetWidth = Next.SetWidth = 36;
        var content = new BoxContainer { SeparationOverride = 12 };
        content.AddChild(_cover);
        content.AddChild(details);
        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        body.AddChild(content);
        body.AddChild(_buttons);
        AddChild(body);
        SetTrack(null, null, null, false);
    }

    public void SetTrack(string? title, string? artist, ResPath? cover, bool canSwitch, bool paused = false, bool canPause = false)
    {
        _title.Text = title ?? Loc.GetString("lobby-state-song-no-song-text");
        _artist.Text = artist ?? string.Empty;
        _title.ToolTip = title;
        _artist.ToolTip = artist;
        Previous.Disabled = Next.Disabled = !canSwitch;
        Pause.Text = Loc.GetString(paused ? "deeplagoon-lobby-music-resume" : "deeplagoon-lobby-music-pause");
        Pause.ToolTip = Loc.GetString(paused ? "deeplagoon-lobby-music-resume-tooltip" : "deeplagoon-lobby-music-pause-tooltip");
        Pause.Disabled = !canPause;
        _cover.Texture = cover is { } path && IoCManager.Resolve<IResourceCache>()
            .TryGetResource<TextureResource>(path, out var resource) ? resource.Texture : null;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var inset = Math.Clamp((availableSize.X - 32) / 2, 0, LobbyConsoleTheme.ContentInset);
        if (PanelOverride is StyleBoxTexture frame)
            frame.SetContentMarginOverride(StyleBox.Margin.Horizontal, inset);
        _cover.Visible = availableSize.X >= 280;
        Previous.Visible = Next.Visible = availableSize.X >= 280;
        _heading.Visible = _buttons.Visible = availableSize.X >= 140;
        return base.MeasureOverride(availableSize);
    }

    private sealed class CoverDisplay : Control
    {
        public Texture? Texture;

        public CoverDisplay()
        {
            SetSize = new Vector2(72);
            VerticalAlignment = VAlignment.Center;
            MouseFilter = MouseFilterMode.Ignore;
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            handle.DrawRect(PixelSizeBox, Color.FromHex("#525A57"));
            if (Texture != null)
            {
                var scale = Math.Min(PixelWidth / (float) Texture.Width, PixelHeight / (float) Texture.Height);
                var size = Texture.Size * scale;
                handle.DrawTextureRect(Texture, UIBox2.FromDimensions((PixelSize - size) / 2, size));
            }
            else
            {
                var scale = 4 * UIScale;
                LobbyPixelText.Draw(handle, "?", (PixelSize - new Vector2(5, 7) * scale) / 2, scale, Color.FromHex("#C5CBC8"));
            }
            handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Metal, false);
        }
    }
}
