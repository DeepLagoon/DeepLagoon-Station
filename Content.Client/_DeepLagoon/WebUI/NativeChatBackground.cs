using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>One native GPU draw for the animated chat area, below the CEF texture.</summary>
public sealed class NativeChatBackground : Control
{
    private ShaderInstance? _shader;
    private int _mode;
    private float _opacity;
    private bool _reduced;
    private Color _base;
    private Color _background;
    private Vector4 _bounds;
    private bool _shaderSettingsDirty = true;
    private Vector2 _shaderOrigin;
    private Vector2 _shaderSize;
    private float _shaderScale;

    public NativeChatBackground()
    {
        HorizontalExpand = VerticalExpand = true;
        MouseFilter = MouseFilterMode.Ignore;
    }

    public void Reset() => _mode = 0;

    public void Configure(string payload)
    {
        if (!TguiActionData.TryParse(payload, out var data) ||
            !data!.TryInt("mode", out var mode) || mode is < 0 or > 11 ||
            !data.TryFloat("opacity", out var opacity) ||
            !data.TryInt("reduced", out var reduced) ||
            !data.TryFloat("left", out var left) || !data.TryFloat("top", out var top) ||
            !data.TryFloat("width", out var width) || !data.TryFloat("height", out var height) ||
            data.String("base") is not { } baseHex || !Color.TryFromHex(baseHex, out var baseColor) ||
            data.String("background") is not { } backgroundHex || !Color.TryFromHex(backgroundHex, out var background)) return;
        _mode = mode;
        _opacity = Math.Clamp(opacity, 0, 1);
        _reduced = reduced != 0;
        _base = baseColor;
        _background = background;
        _bounds = new Vector4(Math.Clamp(left, 0, 1), Math.Clamp(top, 0, 1),
            Math.Clamp(width, 0, 1), Math.Clamp(height, 0, 1));
        _shaderSettingsDirty = true;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (_mode == 0 || Size.X <= 0 || Size.Y <= 0) return;
        // Backing color preserves the theme of the transparent ancestor surfaces.
        handle.DrawRect(UIBox2.FromDimensions(Vector2.Zero, Size), _base);
        var origin = new Vector2(_bounds.X * Width, _bounds.Y * Height);
        var size = Vector2.Min(new Vector2(_bounds.Z * Width, _bounds.W * Height), Size - origin);
        if (size.X <= 0 || size.Y <= 0) return;
        _shader ??= IoCManager.Resolve<IPrototypeManager>().Index<ShaderPrototype>("DeepLagoonChatBackground").InstanceUnique();
        if (_shaderSettingsDirty)
        {
            _shader.SetParameter("mode", _mode);
            _shader.SetParameter("intensity", _opacity);
            _shader.SetParameter("reduced", _reduced ? 1f : 0f);
            _shader.SetParameter("background", _background);
            _shaderSettingsDirty = false;
        }
        var screenOrigin = (GlobalPosition + origin) * UIScale;
        if (_shaderOrigin != screenOrigin || _shaderSize != size || _shaderScale != UIScale)
        {
            _shader.SetParameter("origin", screenOrigin);
            _shader.SetParameter("size", size);
            _shader.SetParameter("ui_scale", UIScale);
            _shaderOrigin = screenOrigin;
            _shaderSize = size;
            _shaderScale = UIScale;
        }
        handle.UseShader(_shader);
        handle.DrawRect(UIBox2.FromDimensions(origin, size), Color.White);
        handle.UseShader(null);
    }

    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        _shader?.Dispose();
        base.Dispose(disposing);
    }
}
