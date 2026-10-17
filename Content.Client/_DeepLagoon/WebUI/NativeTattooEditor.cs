using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Tattoos;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Native tattoo authoring, using server-authorized brush recipes and the existing file policy.</summary>
public sealed class NativeTattooEditor : BoxContainer
{
    private readonly Action<string, string> _send;
    private readonly BoxContainer _editor = NativeInterfacePanel.Column();
    private NativeUiData _state = new();
    private string _id = "", _part = "Chest";
    private int _direction;
    private readonly List<string> _strokes = new();
    private readonly List<Vector2> _points = new();
    private NativePaintCanvas? _canvas;
    public bool Dirty => _strokes.Count > 0 || _points.Count > 0;

    public NativeTattooEditor(Action<string, string> send)
    {
        _send = send; Orientation = LayoutOrientation.Vertical; AddChild(_editor);
    }
    private void Send(string action, TguiData? data = null) => _send(action, (data ?? new TguiData()).ToString());
    public void Update(NativeUiData data)
    {
        _state = data;
        // Server state/status changes must not destroy an unfinished local stroke recipe.
        if (!Dirty) Rebuild();
    }
    private void Rebuild()
    {
        _editor.RemoveAllChildren();
        NativeInterfacePanel.Text(_editor, _state.String("status"));
        var blocked = _state.Bool("disabled") || _state.Bool("pending") || _state.Bool("fileBusy");
        var parts = _state.Array("parts").ToArray();
        if (parts.Length == 0) return;
        if (!parts.Any(p => p.String("id") == _part)) _part = parts[0].String("id");
        NativeInterfacePanel.Options(_editor, parts.Select(p => (p.String("id"), p.String("name"))), id => { if (Dirty) return; _part = id; Rebuild(); }, _part);
        NativeInterfacePanel.Options(_editor, new[] { ("0", "Спереди"), ("1", "Сзади"), ("2", "Справа"), ("3", "Слева") }, value =>
        { if (Dirty) return; _direction = int.Parse(value, CultureInfo.InvariantCulture); Send("tattoo-preview-direction", new TguiData().Number("direction", _direction)); Rebuild(); }, _direction.ToString(CultureInfo.InvariantCulture));
        blocked |= _state.Bool("machine") && parts.First(p => p.String("id") == _part).Bool("covered");
        var tattoos = _state.Array("tattoos").Where(t => t.String("part") == _part && t.Int("direction") == _direction).ToArray();
        var tattoo = tattoos.FirstOrDefault(t => t.String("id") == _id) ?? tattoos.LastOrDefault();
        _id = tattoo?.String("id") ?? "";
        foreach (var item in tattoos)
            NativeInterfacePanel.Button(_editor, "Выбрать тату: " + item.String("id")[..8], () => { if (Dirty) return; _id = item.String("id"); Rebuild(); });
        NativeInterfacePanel.Button(_editor, "Добавить татуировку", () => Send("tattoo-add", new TguiData().String("part", _part).Number("direction", _direction)), !blocked);
        if (tattoo == null) return;
        var fields = NativeInterfacePanel.Row(); _editor.AddChild(fields);
        var x = NativeInterfacePanel.Field(fields, "X", tattoo.String("x")); var y = NativeInterfacePanel.Field(fields, "Y", tattoo.String("y"));
        var scale = NativeInterfacePanel.Field(fields, "Масштаб", tattoo.String("scale"));
        var rotation = NativeInterfacePanel.Field(fields, "Поворот", tattoo.String("rotation"));
        var opacity = NativeInterfacePanel.Field(fields, "Прозрачность", tattoo.String("opacity"));
        TguiData Transform() => new TguiData().String("id", _id).String("x", x.Text).String("y", y.Text).String("scale", scale.Text).String("rotation", rotation.Text).String("opacity", opacity.Text);
        NativeInterfacePanel.Button(_editor, "Применить положение", () => Send("tattoo-transform", Transform()), !blocked);
        var actions = NativeInterfacePanel.Row(); _editor.AddChild(actions);
        foreach (var (action, label) in new[] { ("tattoo-mirror", "Отразить"), ("tattoo-remove", "Удалить"), ("tattoo-export", "Экспорт PNG"), ("tattoo-import", "Импорт PNG") })
            NativeInterfacePanel.Button(actions, label, () => Send(action, new TguiData().String("id", _id)), !blocked);
        var color = NativeInterfacePanel.Field(_editor, "Цвет #RRGGBBAA", "#182030FF");
        var width = NativeInterfacePanel.Field(_editor, "Ширина кисти 0.002–0.2", "0.025");
        var glow = new CheckBox { Text = "Светящиеся чернила" }; var erase = new CheckBox { Text = "Ластик" };
        _editor.AddChild(glow); _editor.AddChild(erase);
        var canvas = new NativePaintCanvas(); _canvas = canvas;
        canvas.Ink = () => Robust.Shared.Maths.Color.TryFromHex(color.Text, out var ink) ? ink : Robust.Shared.Maths.Color.Black;
        canvas.Width = () => float.TryParse(width.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size, .002f, .2f) : .025f;
        using (var image = NativeImage.Decode(tattoo.String("normalBitmap"), Tattoo.Resolution)) if (image != null) canvas.SetImage(image);
        _editor.AddChild(canvas);
        canvas.Stroke += (point, start, end) =>
        {
            if (blocked || _strokes.Count >= 64) return;
            if (start) _points.Clear();
            if (_points.Count < 128) _points.Add(point);
            if (!end) return;
            if (_points.Count == 0 || !float.TryParse(width.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || !float.IsFinite(size)) return;
            var points = "[" + string.Join(',', _points.Select(p => "[" + p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture) + "]")) + "]";
            _strokes.Add("{\"color\":" + GameWebView.Quote(color.Text) + ",\"width\":" + Math.Clamp(size, .002f, .2f).ToString(CultureInfo.InvariantCulture)
                + ",\"glow\":" + (glow.Pressed ? "true" : "false") + ",\"erase\":" + (erase.Pressed ? "true" : "false") + ",\"points\":" + points + "}");
            _points.Clear();
        };
        NativeInterfacePanel.Button(_editor, "Применить рисунок", () =>
        {
            if (!Dirty) return;
            var recipe = "[{\"useBase\":true,\"visible\":true,\"opacity\":1,\"strokes\":[" + string.Join(',', _strokes) + "]}]";
            var payload = Transform().String("recipe", recipe).ToString();
            var count = (payload.Length + 3999) / 4000;
            for (var i = 0; i < count; i++) Send("tattoo-bake-chunk", new TguiData().String("id", _id).Number("index", i).Number("count", count)
                .String("text", payload.Substring(i * 4000, Math.Min(4000, payload.Length - i * 4000))));
            _strokes.Clear(); _points.Clear();
        }, !blocked);
        NativeInterfacePanel.Button(_editor, Loc.GetString("dl-ui-native-reset"), () => { _strokes.Clear(); _points.Clear(); Rebuild(); });
    }
}
