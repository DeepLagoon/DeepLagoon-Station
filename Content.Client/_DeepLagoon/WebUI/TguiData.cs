using System.Globalization;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Small typed JSON builder for content-owned TGUI panels in the client sandbox.</summary>
public sealed class TguiData
{
    private readonly Dictionary<string, string> _fields = new();
    public TguiData String(string key, string? value) => Raw(key, GameWebView.Quote(value ?? ""));
    public TguiData Number(string key, int value) => Raw(key, value.ToString(CultureInfo.InvariantCulture));
    public TguiData Number(string key, float value) => Raw(key, (float.IsFinite(value) ? value : 0).ToString(CultureInfo.InvariantCulture));
    public TguiData Bool(string key, bool value) => Raw(key, value ? "true" : "false");
    public TguiData Object(string key, TguiData value) => Raw(key, value.ToString());
    public TguiData Array(string key, IEnumerable<TguiData> values) => Raw(key, "[" + string.Join(',', values) + "]");
    /// <summary>A value already serialized by TguiData (for example a cached array).</summary>
    public TguiData Json(string key, string json) => Raw(key, json);
    private TguiData Raw(string key, string value)
    {
        _fields[key] = GameWebView.Quote(key) + ":" + value;
        return this;
    }
    public override string ToString() => "{" + string.Join(',', _fields.Values) + "}";
}
