using System.Numerics;
using Content.Shared.CCVar;
using Robust.Client.Audio;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Audio.Sources;
using Robust.Shared.Configuration;
using Robust.Shared;

namespace Content.Client.Lobby.UI;

/// <summary>
/// Native button artwork: DMI flick animations play once, while hover, disabled and
/// ready states follow the existing SS14 button. The original button retains input and text.
/// </summary>
public sealed class LobbyButtonArtwork : Control
{
    private readonly Button _button;
    private readonly Func<(RSI Rsi, string State)>? _select;
    private RSI.State? _animation;
    private RSI.State? _display;
    private int _frame;
    private float _elapsed;
    private bool _wasDisabled;
    private (RSI Rsi, string State)? _pressStart;
    private IAudioSource? _clickSource;
    private readonly bool _utility;
    private float _clickFlash;
    private readonly bool _nativeText;
    private readonly bool _preserveTooltip;

    private LobbyButtonArtwork(Button button, Func<(RSI, string)>? select, bool utility = false, bool nativeText = false, bool preserveTooltip = false)
    {
        _button = button;
        _select = select;
        _utility = utility;
        _nativeText = nativeText;
        _preserveTooltip = preserveTooltip;
        _wasDisabled = button.Disabled;
        MouseFilter = MouseFilterMode.Ignore;
        button.OnButtonDown += OnButtonDown;
        button.OnPressed += OnPressed;
        if (utility)
            RectClipContent = true;
    }

    public static void AttachUtility(Button button, bool nativeText = false, bool preserveTooltip = false)
    {
        Attach(button, null, utility: true, nativeText: nativeText, preserveTooltip: preserveTooltip);
    }

    public static void Attach(Button button, string asset, Func<string> state)
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        var rsi = cache.GetResource<RSIResource>($"/Textures/_DeepLagoon/Interface/Lobby/{asset}.rsi").RSI;
        Attach(button, () => (rsi, state()));
    }

    public static void AttachReady(Button button)
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        var ready = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/ready.rsi").RSI;
        var join = cache.GetResource<RSIResource>("/Textures/_DeepLagoon/Interface/Lobby/join.rsi").RSI;
        Attach(button, () => button.ToggleMode ? (ready, button.Pressed ? "ready" : "not_ready") : (join, "join_game"));
    }

    private static void Attach(Button button, Func<(RSI, string)>? select, bool utility = false, bool nativeText = false, bool preserveTooltip = false)
    {
        button.StyleBoxOverride = new StyleBoxEmpty();
        button.MuteSounds = true;
        // Keep Text available to LobbyState, but don't render duplicate captions.
        button.Label.Visible = nativeText;
        if (!preserveTooltip)
            button.ToolTip = button.Text;
        if (nativeText)
        {
            button.Label.Orphan();
            button.Label.ClipText = true;
            button.Label.Margin = new Thickness(button.ToggleMode ? 24 : 8, 6, 8, 6);
            button.Label.Align = Label.AlignMode.Left;
            button.Label.OutlineThicknessOverride = 0;
        }
        button.AddChild(new LobbyButtonArtwork(button, select, utility, nativeText, preserveTooltip));
        if (nativeText)
            button.AddChild(button.Label);
    }

    private void OnButtonDown(BaseButton.ButtonEventArgs args)
    {
        // BYOND flicks the state being left, before toggling readiness.
        _pressStart = _select?.Invoke();
    }

    private void OnPressed(BaseButton.ButtonEventArgs args)
    {
        var selected = _pressStart ?? _select?.Invoke();
        _pressStart = null;
        _clickFlash = 0.12f;
        if (selected is { } art)
            PlayOnce(art.Rsi, art.State + "_pressed");
        PlayClickSound();
    }

    private void PlayClickSound()
    {
        var config = IoCManager.Resolve<IConfigurationManager>();
        if (_button.Disabled || !config.GetCVar(CVars.InterfaceAudio))
            return;

        if (_clickSource == null)
        {
            var sound = IoCManager.Resolve<IResourceCache>()
                .GetResource<AudioResource>("/Audio/_DeepLagoon/UserInterface/lobby_click.ogg");
            _clickSource = IoCManager.Resolve<IAudioManager>().CreateAudioSource(sound);
        }

        if (_clickSource == null)
            return;

        _clickSource.Global = true;
        _clickSource.Gain = 0.25f * config.GetCVar(CCVars.InterfaceVolume);
        _clickSource.Restart();
    }

    protected override void ExitedTree()
    {
        base.ExitedTree();
        _clickSource?.Dispose();
        _clickSource = null;
    }

    private void PlayOnce(RSI rsi, string state)
    {
        if (!rsi.TryGetState(state, out var animation))
            return;
        _animation = animation;
        _frame = 0;
        _elapsed = 0;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_utility)
            return new Vector2(0, _nativeText ? 34 : LobbyConsoleTheme.RowHeight);
        var (rsi, _) = _select!.Invoke();
        return rsi.Size;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!VisibleInTree)
            return;
        _clickFlash = Math.Max(0, _clickFlash - args.DeltaSeconds);

        if (!_preserveTooltip && _button.ToolTip != _button.Text)
            _button.ToolTip = _button.Text;
        if (_utility)
            return;
        var (rsi, state) = _select!.Invoke();
        if (_button.Disabled || _animation?.RSI != rsi)
            _animation = null;
        if (_wasDisabled && !_button.Disabled)
            PlayOnce(rsi, state + "_enabled");
        _wasDisabled = _button.Disabled;

        if (_animation != null)
        {
            _elapsed += args.DeltaSeconds;
            while (_animation != null && _elapsed >= _animation.GetDelay(_frame))
            {
                _elapsed -= _animation.GetDelay(_frame);
                if (++_frame >= _animation.DelayCount)
                {
                    _animation = null;
                    _frame = 0;
                }
            }
        }

        var suffix = _button.Disabled ? "_disabled"
            : _button.IsHovered ? "_highlighted" : "";
        if (!rsi.TryGetState(state + suffix, out var idle))
            idle = rsi[state];
        _display = _animation ?? idle;
        ModulateSelfOverride = _button.Disabled && suffix == "_disabled" && !rsi.TryGetState(state + suffix, out _)
            ? Color.Gray : Color.White;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (_utility)
        {
            var pressed = _clickFlash > 0 || _button.Pressed;
            var hovered = _button.IsHovered || _button.HasKeyboardFocus();
            var color = _button.Disabled ? LobbyConsoleTheme.Green.WithAlpha(0.28f)
                : !_nativeText && pressed ? LobbyConsoleTheme.Screen
                : hovered || _button.StyleClasses.Contains("Caution") ? LobbyConsoleTheme.Hover
                : LobbyConsoleTheme.Green;
            if (!_button.Disabled)
            {
                if (!_nativeText && pressed)
                    handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Green);
                else if (pressed || hovered)
                    handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Green.WithAlpha(_nativeText ? 0.10f : 0.12f));
                if (_nativeText && _button.Pressed)
                    handle.DrawRect(PixelSizeBox, LobbyConsoleTheme.Green.WithAlpha(0.26f), false);
            }
            // Keep ChangelogButton's unread marker available in its tooltip, while
            // retaining the approved short screen caption and green palette.
            var text = (_button is Content.Client.Changelog.ChangelogButton
                ? Loc.GetString("changelog-button") : _button.Text ?? "").Trim();
            var cells = Math.Max(1, text.Length * 6 - 1);
            if (_nativeText)
            {
                _button.Label.FontColorOverride = color;
                if (_button.ToggleMode)
                {
                    var marker = UIBox2.FromDimensions(8 * UIScale, (PixelHeight - 6 * UIScale) / 2, 6 * UIScale, 6 * UIScale);
                    handle.DrawRect(marker, color.WithAlpha(_button.Pressed ? 1 : 0.5f), _button.Pressed);
                }
                return;
            }
            if (Width <= 14)
                return;
            var captionScale = Math.Min(2, (Width - 14) / cells) * UIScale;
            var captionOrigin = new Vector2(MathF.Floor((PixelWidth - cells * captionScale) / 2), MathF.Floor((PixelHeight - 7 * captionScale) / 2));
            if (!pressed && !_button.Disabled)
                LobbyPixelText.Draw(handle, text, captionOrigin + new Vector2(UIScale), captionScale, color.WithAlpha(0.15f));
            LobbyPixelText.Draw(handle, text, captionOrigin, captionScale, color);
            return;
        }
        if (_display == null)
            return;
        var texture = _display.GetFrame(RsiDirection.South, _animation != null ? _frame : 0);
        var fit = MathF.Min(PixelWidth / (float) texture.Width, PixelHeight / (float) texture.Height);
        var scale = Math.Clamp(MathF.Round(fit / UIScale), 1, 2) * UIScale;
        var size = texture.Size * scale;
        var offset = (PixelSize - size) / 2;
        var origin = new Vector2(MathF.Floor(offset.X), MathF.Floor(offset.Y));
        handle.DrawTextureRect(texture, UIBox2.FromDimensions(origin, size));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clickSource?.Dispose();
            _clickSource = null;
            _button.OnButtonDown -= OnButtonDown;
            _button.OnPressed -= OnPressed;
        }
        base.Dispose(disposing);
    }
}
