using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Art;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Utility;
using Robust.Shared.Input;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Native raster editor with local layers, masks, undo, reset and explicit save.</summary>
public sealed class NativeArtEditor : BoxContainer
{
    private sealed class Layer : IDisposable
    {
        public string Name = "Слой";
        public bool Visible = true;
        public float Opacity = 1;
        public Image<Rgba32> Image = new(ArtLimits.Resolution, ArtLimits.Resolution);
        public Image<Rgba32>? Mask;
        public void Dispose() { Image.Dispose(); Mask?.Dispose(); }
    }
    private readonly List<Layer> _layers = new();
    private readonly Stack<string> _undo = new(), _redo = new();
    private readonly Action<string, string> _send;
    private readonly NativePaintCanvas _canvas = new();
    private readonly OptionButton _layer = new();
    private readonly LineEdit _title, _color, _size, _layerName, _opacity;
    private readonly CheckBox _erase = new() { Text = "Ластик" }, _mask = new() { Text = "Рисовать маску слоя" };
    private readonly Label _status = new();
    private string _saved = "";
    private string _submitted = "";
    private int _revision = -1, _latestRevision = -1, _selected;
    private bool _saving;
    private Vector2 _last;
    public bool Dirty { get; private set; }

    public NativeArtEditor(Action<string, string> send)
    {
        _send = send; Orientation = LayoutOrientation.Vertical;
        _title = NativeInterfacePanel.Field(this, "Название работы");
        _title.OnTextChanged += _ => Dirty = true;
        AddChild(_status);
        var tools = NativeInterfacePanel.Row(); AddChild(tools);
        _color = NativeInterfacePanel.Field(tools, "Цвет #RRGGBBAA", "#182030FF");
        _size = NativeInterfacePanel.Field(tools, "Размер кисти, пикс.", "12");
        _canvas.Ink = () => Color.TryFromHex(_color.Text, out var ink) ? ink : Color.Black;
        _canvas.Width = () => float.TryParse(_size.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size / ArtLimits.Resolution, .002f, .2f) : .02f;
        tools.AddChild(_erase); tools.AddChild(_mask);
        AddChild(_canvas);
        _canvas.Stroke += Paint;
        AddChild(_layer);
        _layer.OnItemSelected += args => { _selected = args.Id; _layer.SelectId(args.Id); RefreshFields(); };
        var layerTools = NativeInterfacePanel.Row(); AddChild(layerTools);
        _layerName = NativeInterfacePanel.Field(layerTools, "Имя слоя");
        _opacity = NativeInterfacePanel.Field(layerTools, "Прозрачность 0–1", "1");
        NativeInterfacePanel.Button(layerTools, "Изменить слой", () =>
        {
            if (_layers.Count == 0 || !float.TryParse(_opacity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) || !float.IsFinite(opacity)) return;
            Remember(); var layer = _layers[_selected]; layer.Name = _layerName.Text; layer.Opacity = Math.Clamp(opacity, 0, 1); Changed();
        });
        var layers = NativeInterfacePanel.Row(); AddChild(layers);
        NativeInterfacePanel.Button(layers, "+ Слой", () => { if (_layers.Count >= ArtLimits.MaxLayers) return; Remember(); _layers.Add(new Layer { Name = "Слой " + (_layers.Count + 1) }); _selected = _layers.Count - 1; Changed(); });
        NativeInterfacePanel.Button(layers, "Удалить слой", () => { if (_layers.Count <= 1) return; Remember(); _layers[_selected].Dispose(); _layers.RemoveAt(_selected); _selected = Math.Max(0, _selected - 1); Changed(); });
        NativeInterfacePanel.Button(layers, "Видимость", () => { Remember(); _layers[_selected].Visible ^= true; Changed(); });
        foreach (var delta in new[] { -1, 1 }) NativeInterfacePanel.Button(layers, delta < 0 ? "↓" : "↑", () =>
        {
            var next = _selected + delta; if (next < 0 || next >= _layers.Count) return;
            Remember(); (_layers[next], _layers[_selected]) = (_layers[_selected], _layers[next]); _selected = next; Changed();
        });
        NativeInterfacePanel.Button(layers, "Снять маску", () => { Remember(); _layers[_selected].Mask?.Dispose(); _layers[_selected].Mask = null; Changed(); });
        var controls = NativeInterfacePanel.Row(); AddChild(controls);
        NativeInterfacePanel.Button(controls, "Отменить", () => { if (!_undo.TryPop(out var document)) return; _redo.Push(Document()); Load(document); Dirty = true; });
        NativeInterfacePanel.Button(controls, "Вернуть", () => { if (!_redo.TryPop(out var document)) return; _undo.Push(Document()); Load(document); Dirty = true; });
        NativeInterfacePanel.Button(controls, Loc.GetString("dl-ui-native-reset"), () => { Load(_saved); _revision = _latestRevision; Dirty = false; _undo.Clear(); _redo.Clear(); _send("discard", "{}"); });
        NativeInterfacePanel.Button(controls, "Сохранить", Save);
    }

    public void Update(NativeUiData data)
    {
        _status.Text = data.String("status");
        if (_saving && data.Int("result") < 0) _saving = false;
        if (data.Int("revision") == _revision) return;
        _latestRevision = data.Int("revision");
        _saved = data.String("document", ArtLimits.EmptyDocument);
        if (Dirty && !_saving) { _status.Text = "Работа на сервере изменилась. Сбросьте черновик для загрузки новой версии."; return; }
        _revision = data.Int("revision");
        if (_saving && Document() != _submitted) { _saving = false; return; }
        Load(_saved); Dirty = false; _saving = false; _undo.Clear(); _redo.Clear();
    }

    private void Remember()
    {
        if (_undo.Count >= 12) _undo.Clear();
        _undo.Push(Document()); _redo.Clear();
    }
    private void Paint(Vector2 point, bool start, bool end)
    {
        if (_layers.Count == 0 || _saving) return;
        if (start) { Remember(); _last = point; }
        if (!Color.TryFromHex(_color.Text, out var color)) return;
        if (!float.TryParse(_size.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius) || !float.IsFinite(radius)) return;
        radius = Math.Clamp(radius / 2, 1, 96);
        var layer = _layers[_selected];
        if (_mask.Pressed && layer.Mask == null) layer.Mask = new Image<Rgba32>(ArtLimits.Resolution, ArtLimits.Resolution, new Rgba32(255, 255, 255, 255));
        var image = _mask.Pressed ? layer.Mask! : layer.Image;
        var pixels = image.GetPixelSpan();
        var from = _last * ArtLimits.Resolution; var to = point * ArtLimits.Resolution;
        var steps = Math.Clamp((int)Vector2.Distance(from, to) + 1, 1, 2048);
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(from, to, (float)i / steps);
            for (var y = Math.Max(0, (int)(p.Y - radius)); y < Math.Min(image.Height, p.Y + radius + 1); y++)
            for (var x = Math.Max(0, (int)(p.X - radius)); x < Math.Min(image.Width, p.X + radius + 1); x++)
            {
                if (Vector2.DistanceSquared(new Vector2(x, y), p) > radius * radius) continue;
                pixels[y * image.Width + x] = _mask.Pressed
                    ? new Rgba32((byte)(_erase.Pressed ? 0 : 255), (byte)(_erase.Pressed ? 0 : 255), (byte)(_erase.Pressed ? 0 : 255), 255)
                    : _erase.Pressed ? default : new Rgba32(color.R, color.G, color.B, color.A);
            }
        }
        _last = point; Dirty = true;
        if (end) RefreshImage();
    }
    private void Changed() { Dirty = true; RefreshLayers(); RefreshImage(); }
    private void RefreshFields()
    {
        if (_layers.Count == 0) return;
        _layerName.Text = _layers[_selected].Name;
        _opacity.Text = _layers[_selected].Opacity.ToString(CultureInfo.InvariantCulture);
    }
    private void RefreshLayers()
    {
        _layer.Clear();
        for (var i = 0; i < _layers.Count; i++) _layer.AddItem((_layers[i].Visible ? "● " : "○ ") + _layers[i].Name, i);
        if (_layers.Count > 0) _layer.SelectId(_selected);
        RefreshFields();
    }
    private void Load(string document)
    {
        foreach (var layer in _layers) layer.Dispose(); _layers.Clear();
        var data = NativeUiData.Parse(document); _title.Text = data.String("title");
        foreach (var item in data.Array("layers").Take(ArtLimits.MaxLayers))
        {
            var layer = new Layer { Name = item.String("name"), Visible = item.Bool("visible"), Opacity = Math.Clamp(item.Float("opacity"), 0, 1) };
            var png = NativeImage.Decode(item.String("png"), ArtLimits.Resolution);
            if (png != null) { layer.Image.Dispose(); layer.Image = png; }
            layer.Mask = NativeImage.Decode(item.String("mask"), ArtLimits.Resolution); _layers.Add(layer);
        }
        if (_layers.Count == 0) _layers.Add(new Layer());
        _selected = Math.Min(_selected, _layers.Count - 1); RefreshLayers(); RefreshImage();
    }
    private string Document() => new TguiData().Number("version", 1).String("title", _title.Text).Array("layers", _layers.Select(layer => new TguiData()
        .String("name", layer.Name).Bool("visible", layer.Visible).Number("opacity", layer.Opacity)
        .String("png", NativeImage.Encode(layer.Image)).String("mask", layer.Mask == null ? "" : NativeImage.Encode(layer.Mask)))).ToString();

    private Image<Rgba32> Composite()
    {
        var result = new Image<Rgba32>(ArtLimits.Resolution, ArtLimits.Resolution);
        var destination = result.GetPixelSpan();
        foreach (var layer in _layers.Where(l => l.Visible))
        {
            var source = layer.Image.GetPixelSpan();
            ReadOnlySpan<Rgba32> maskPixels = layer.Mask == null ? ReadOnlySpan<Rgba32>.Empty : layer.Mask.GetPixelSpan();
            for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
            {
                var offset = y * result.Width + x;
                var src = source[offset]; var dst = destination[offset];
                var alpha = src.A / 255f * layer.Opacity * (maskPixels.IsEmpty ? 1 : maskPixels[offset].R / 255f * maskPixels[offset].A / 255f);
                var outAlpha = alpha + dst.A / 255f * (1 - alpha);
                if (outAlpha <= 0) continue;
                destination[offset] = new Rgba32((byte)((src.R * alpha + dst.R * dst.A / 255f * (1 - alpha)) / outAlpha),
                    (byte)((src.G * alpha + dst.G * dst.A / 255f * (1 - alpha)) / outAlpha),
                    (byte)((src.B * alpha + dst.B * dst.A / 255f * (1 - alpha)) / outAlpha), (byte)(outAlpha * 255));
            }
        }
        return result;
    }
    private void RefreshImage() { using var image = Composite(); _canvas.SetImage(image); }
    private void Save()
    {
        if (_saving || !Dirty) return;
        var document = Document();
        if (document.Length > ArtLimits.MaxDocumentChars) { _status.Text = "Рисунок слишком большой."; return; }
        _saving = true;
        _submitted = document;
        var count = (document.Length + ArtLimits.ChunkChars - 1) / ArtLimits.ChunkChars;
        for (var index = 0; index < count; index++)
            _send("save", new TguiData().Number("index", index).Number("count", count)
                .String("text", document.Substring(index * ArtLimits.ChunkChars, Math.Min(ArtLimits.ChunkChars, document.Length - index * ArtLimits.ChunkChars))).ToString());
    }
    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing) { if (disposing) foreach (var layer in _layers) layer.Dispose(); base.Dispose(disposing); }
}

/// <summary>Owned texture control; textures are released when an old native view leaves the tree.</summary>
public class NativeImage : Control
{
    private OwnedTexture? _texture;
    public NativeImage() { SetSize = new Vector2(256); MouseFilter = MouseFilterMode.Ignore; }
    public virtual void SetImage(Image<Rgba32> image)
    {
        var texture = (OwnedTexture)Texture.LoadFromImage(image, "native-interface-image");
        _texture?.Dispose(); _texture = texture;
    }
    public static void Add(Control parent, string data)
    {
        using var image = Decode(data);
        if (image == null) return;
        var view = new NativeImage(); view.SetImage(image); parent.AddChild(view);
    }
    public static Image<Rgba32>? Decode(string data, int expected = 0)
    {
        if (data.Length == 0 || data.Length > 8 * 1024 * 1024) return null;
        try
        {
            if (data.StartsWith("data:", StringComparison.Ordinal)) data = data[(data.IndexOf(',') + 1)..];
            var bytes = Convert.FromBase64String(data);
            // PNG dimensions are checked before decoding, using the sandbox's existing stream API.
            if (bytes.Length > 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
            {
                int Number(int p) => bytes[p] << 24 | bytes[p + 1] << 16 | bytes[p + 2] << 8 | bytes[p + 3];
                var width = Number(16); var height = Number(20);
                if (width is < 1 or > 2048 || height is < 1 or > 2048 || expected > 0 && (width != expected || height != expected)) return null;
            }
            using var input = new MemoryStream(bytes);
            var image = Image.Load<Rgba32>(input);
            if (image.Width > 2048 || image.Height > 2048 || expected > 0 && (image.Width != expected || image.Height != expected)) { image.Dispose(); return null; }
            return image;
        }
        catch (Exception) { return null; }
    }
    public static string Encode(Image<Rgba32> image)
    {
        using var stream = new MemoryStream(); image.SaveAsPng(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
    protected override void Draw(DrawingHandleScreen handle)
    {
        handle.DrawRect(PixelSizeBox, Color.FromHex("#F7F0DD"));
        if (_texture != null) handle.DrawTextureRect(_texture, PixelSizeBox);
    }
    protected override void ExitedTree() { _texture?.Dispose(); _texture = null; base.ExitedTree(); }
    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing) { _texture?.Dispose(); _texture = null; base.Dispose(disposing); }
}

public sealed class NativePaintCanvas : NativeImage
{
    public event Action<Vector2, bool, bool>? Stroke;
    private bool _painting;
    private Vector2 _last;
    private readonly List<(Vector2 From, Vector2 To)> _preview = new();
    public Func<Color> Ink { get; set; } = () => Color.Black;
    public Func<float> Width { get; set; } = () => 0.02f;
    public override void SetImage(Image<Rgba32> image) { base.SetImage(image); _preview.Clear(); }
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var color = Ink(); var radius = Math.Clamp(Width(), .002f, .2f) * PixelSize.X / 2;
        foreach (var (from, to) in _preview)
        {
            var steps = Math.Clamp((int)Vector2.Distance(from * PixelSize, to * PixelSize), 1, 600);
            for (var i = 0; i <= steps; i++) handle.DrawCircle(Vector2.Lerp(from, to, (float)i / steps) * PixelSize, radius, color);
        }
    }
    public NativePaintCanvas() { SetSize = new Vector2(400); MouseFilter = MouseFilterMode.Stop; }
    private Vector2 Point(Vector2 value) => Vector2.Clamp(value / Size, Vector2.Zero, Vector2.One);
    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick) return;
        _painting = true; _last = Point(args.RelativePosition); _preview.Add((_last, _last)); Stroke?.Invoke(_last, true, false); args.Handle();
    }
    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (!_painting) return;
        var point = Point(args.RelativePosition); if (_preview.Count < 8192) _preview.Add((_last, point));
        _last = point; Stroke?.Invoke(_last, false, false);
    }
    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function != EngineKeyFunctions.UIClick || !_painting) return;
        _painting = false; Stroke?.Invoke(_last, false, true); args.Handle();
    }
    protected override void MouseExited()
    {
        base.MouseExited();
        if (!_painting) return;
        _painting = false; Stroke?.Invoke(_last, false, true);
    }
}
