using System.Globalization;
using System.Linq;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Native coordinate/catalogue editor; changes stay local until Apply.</summary>
public sealed partial class NativeApartmentEditor : BoxContainer
{
    private readonly Action<string, string> _send;
    private readonly BoxContainer _list = NativeInterfacePanel.Column();
    private readonly BoxContainer _catalog = NativeInterfacePanel.Column();
    private readonly Label _status = new();
    private readonly Label _roomTitle = new();
    private readonly NativeApartmentPlan _plan = new();
    private readonly LineEdit _x, _y, _rotation;
    private readonly List<TguiData> _draft = new();
    private readonly List<string> _ids = new();
    private NativeUiData _state = new();
    private string _furniture = "";
    private int _revision = -1;
    private bool _applying;
    public bool Dirty { get; private set; }

    public NativeApartmentEditor(Action<string, string> send)
    {
        _send = send; Orientation = LayoutOrientation.Vertical;
        AddChild(_roomTitle);
        var zoom = NativeInterfacePanel.Row(); AddChild(zoom);
        var scroll = new ScrollContainer { MinHeight = 270, MaxHeight = 420, HorizontalExpand = true };
        scroll.AddChild(_plan); AddChild(scroll);
        var scale = 1f;
        void Zoom(float value) { scale = Math.Clamp(value, .5f, 4f); _plan.MinSize = new System.Numerics.Vector2(300, 250) * scale; }
        NativeInterfacePanel.Button(zoom, "−", () => Zoom(scale / 1.25f));
        NativeInterfacePanel.Button(zoom, "+", () => Zoom(scale * 1.25f));
        NativeInterfacePanel.Button(zoom, "Сбросить масштаб", () => Zoom(1));
        AddChild(_status); InitializeCommerce(); AddChild(_catalog);
        var fields = NativeInterfacePanel.Row(); AddChild(fields);
        _x = NativeInterfacePanel.Field(fields, "X", "0"); _y = NativeInterfacePanel.Field(fields, "Y", "0");
        _rotation = NativeInterfacePanel.Field(fields, "Поворот (0 / 90 / 180 / 270)", "0");
        NativeInterfacePanel.Button(this, "Добавить мебель в черновик", () =>
        {
            if (_applying || _furniture.Length == 0 || !int.TryParse(_x.Text, out var x) || !int.TryParse(_y.Text, out var y)
                || !int.TryParse(_rotation.Text, out var rotation) || rotation < 0 || rotation > 270 || rotation % 90 != 0) return;
            if (!CanAddFurniture()) { _status.Text = "Нет свободных экземпляров этой модели. Добавьте ещё один набор в каталоге."; return; }
            var id = Guid.NewGuid().ToString("N");
            _ids.Add(id); _draft.Add(new TguiData().String("id", id).String("furniture", _furniture).Number("x", x).Number("y", y).Number("rotation", rotation / 90));
            Changed();
        });
        AddChild(_list);
        var controls = NativeInterfacePanel.Row(); AddChild(controls);
        NativeInterfacePanel.Button(controls, Loc.GetString("dl-ui-native-apply"), () => { if (_applying) return; _applying = true; Send("apply", Layout()); });
        NativeInterfacePanel.Button(controls, Loc.GetString("dl-ui-native-reset"), Reset);
        NativeInterfacePanel.Button(controls, "Шаблоны квартир", () => Send("shop"));
        NativeInterfacePanel.Button(controls, "До", () => Send("before"));
        NativeInterfacePanel.Button(controls, "После", () => Send("after"));
        NativeInterfacePanel.Button(controls, "Выйти из квартиры", () => { if (Dirty) _status.Text = Loc.GetString("dl-ui-native-unsaved"); else Send("exit"); });
    }

    public void Update(NativeUiData data)
    {
        if (data.Int("revision") == _revision && data.String("token") == _state.String("token"))
        {
            _state = data; UpdateCommerce(); PlacementOptions(); Refresh(); return;
        }
        if (data.String("number") != _state.String("number") || data.String("template") != _state.String("template")) Dirty = false;
        _state = data;
        UpdateCommerce();
        _roomTitle.Text = data.String("name") + " · Квартира " + data.String("number");
        if (Dirty && !_applying) { _status.Text = "Планировка на сервере изменилась. Сбросьте черновик для загрузки новой версии."; return; }
        _revision = data.Int("revision"); _applying = false;
        PlacementOptions();
        Reset();
    }

    public void Receive(NativeUiData data)
    {
        var message = data.String("status", data.String("message"));
        if (message.Length > 0) { _status.Text = message; _applying = false; }
        if (data.Bool("success")) { Dirty = false; _applying = false; }
    }
    private void Reset()
    {
        _revision = _state.Int("revision"); _draft.Clear(); _ids.Clear();
        foreach (var p in _state.Array("layout"))
        {
            _ids.Add(p.String("id"));
            _draft.Add(new TguiData().String("id", p.String("id")).String("furniture", p.String("furniture"))
                .Number("x", p.Int("x")).Number("y", p.Int("y")).Number("rotation", p.Int("rotation")));
        }
        Dirty = false; Refresh(); Send("preview", Layout());
    }
    private TguiData Layout() => new TguiData().Array("layout", _draft);
    private void Changed() { Dirty = true; Refresh(); Send("preview", Layout()); }
    private void Send(string action, TguiData? data = null) => _send(action, (data ?? new TguiData()).ToString());
    private void Refresh()
    {
        _plan.Update(_state, _draft);
        _list.RemoveAllChildren();
        for (var i = 0; i < _draft.Count; i++)
        {
            var index = i; var p = NativeUiData.Parse(_draft[i].ToString());
            var row = NativeInterfacePanel.Row(); _list.AddChild(row);
            var definition = _state.Array("catalog").FirstOrDefault(item => item.String("id") == p.String("furniture"));
            NativeInterfacePanel.Text(row, $"{definition?.String("name") ?? p.String("furniture")} ({p.String("x")}, {p.String("y")}) {p.Int("rotation") * 90}°");
            if (definition?.Bool("existingOnly") != true) NativeInterfacePanel.Button(row, "Удалить", () => { if (_applying) return; _draft.RemoveAt(index); _ids.RemoveAt(index); Changed(); });
            NativeInterfacePanel.Button(row, "Переместить", () =>
            {
                if (_applying || !int.TryParse(_x.Text, out var x) || !int.TryParse(_y.Text, out var y) || !int.TryParse(_rotation.Text, out var rotation) || rotation < 0 || rotation > 270 || rotation % 90 != 0) return;
                _draft[index] = new TguiData().String("id", _ids[index]).String("furniture", p.String("furniture")).Number("x", x).Number("y", y).Number("rotation", rotation / 90);
                Changed();
            });
        }
    }
}

/// <summary>Native mapper editor, using the same bounded upload protocol and server validation.</summary>
public sealed class NativeGridEditor : BoxContainer
{
    private readonly Action<string, string> _send;
    private readonly Label _status = new();
    private readonly BoxContainer _catalog = NativeInterfacePanel.Column(), _list = NativeInterfacePanel.Column();
    private readonly LineEdit _x, _y, _rotation, _search, _regionX, _regionY;
    private readonly Dictionary<string, TguiData> _changes = new();
    private NativeUiData _state = new();
    private string _prototype = "Plating", _kind = "tile", _token = "";
    private NativeUiData? _pendingState;
    private int _page;
    private bool _applying;
    private TguiData[] _upload = [];
    private string _uploadId = "";
    public bool Dirty => _changes.Count > 0;

    public NativeGridEditor(Action<string, string> send)
    {
        _send = send; Orientation = LayoutOrientation.Vertical;
        AddChild(_status);
        var region = NativeInterfacePanel.Row(); AddChild(region);
        _regionX = NativeInterfacePanel.Field(region, "Регион X", "0"); _regionY = NativeInterfacePanel.Field(region, "Регион Y", "0");
        NativeInterfacePanel.Button(region, "Загрузить регион", () =>
        {
            if (Dirty) { _status.Text = Loc.GetString("dl-ui-native-unsaved"); return; }
            if (int.TryParse(_regionX.Text, out var x) && int.TryParse(_regionY.Text, out var y)) Send("region", new TguiData().Number("x", x).Number("y", y));
        });
        _search = NativeInterfacePanel.Field(this, "Поиск прототипов");
        NativeInterfacePanel.Button(this, "Найти", () => Send("search", new TguiData().String("query", _search.Text).String("category", "all").Number("page", 0)));
        AddChild(_catalog);
        var fields = NativeInterfacePanel.Row(); AddChild(fields);
        _x = NativeInterfacePanel.Field(fields, "X", "0"); _y = NativeInterfacePanel.Field(fields, "Y", "0");
        _rotation = NativeInterfacePanel.Field(fields, "Поворот, градусы", "0");
        NativeInterfacePanel.Button(this, "Разместить в черновике", () =>
        {
            if (_applying || !Number(_x.Text, out var x) || !Number(_y.Text, out var y) || !Number(_rotation.Text, out var rotation)) return;
            if (_kind == "tile") { x = MathF.Floor(x); y = MathF.Floor(y); }
            else if (_kind == "entity") { x = MathF.Floor(x) + .5f; y = MathF.Floor(y) + .5f; }
            var id = _kind == "tile" ? $"t:{(int)x}:{(int)y}" : "new_" + Guid.NewGuid().ToString("N");
            _changes[id] = new TguiData().String("id", id).String("kind", _kind).String("prototype", _prototype)
                .Number("x", x).Number("y", y).Number("rotation", rotation).String("color", "#FFFFFFFF").Bool("anchored", true);
            Refresh();
        });
        AddChild(_list);
        var controls = NativeInterfacePanel.Row(); AddChild(controls);
        NativeInterfacePanel.Button(controls, "◀", () => { _page = Math.Max(0, _page - 1); Refresh(); });
        NativeInterfacePanel.Button(controls, "▶", () => { _page++; Refresh(); });
        NativeInterfacePanel.Button(controls, Loc.GetString("dl-ui-native-apply"), Apply);
        NativeInterfacePanel.Button(controls, Loc.GetString("dl-ui-native-reset"), () =>
        {
            if (_applying) return;
            _changes.Clear();
            if (_pendingState != null) { var data = _pendingState; _pendingState = null; _token = ""; Update(data); }
            else Refresh();
        });
        NativeInterfacePanel.Button(controls, "Экспорт карты", () => Send("download", new TguiData().String("scope", "map")));
        NativeInterfacePanel.Button(controls, "Восстановить атмосферу", () => Send("fixAtmos"));
    }

    private static bool Number(string value, out float number) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
    private void Send(string action, TguiData? data = null) => _send(action, (data ?? new TguiData()).ToString());
    public void Update(NativeUiData data)
    {
        if (_token == data.String("token")) return;
        if (Dirty && !_applying) { _pendingState = data; _status.Text = "Регион на сервере изменился. Сбросьте черновик для загрузки новой версии."; return; }
        _state = data; _token = data.String("token"); _changes.Clear(); _applying = false; _page = 0;
        _regionX.Text = data.String("x"); _regionY.Text = data.String("y"); Refresh();
    }

    public void Receive(NativeUiData data)
    {
        if (data.String("status").Length > 0) { _status.Text = data.String("status"); _applying = false; }
        if (data.String("uploadId") == _uploadId && _uploadId.Length > 0)
        {
            var index = data.Int("uploadNext");
            if (index < Math.Max(1, (_upload.Length + 15) / 16)) UserInterfaceManager.DeferAction(() => SendChunk(index));
        }
        var catalog = data.Array("catalog").ToArray();
        if (catalog.Length == 0) return;
        _catalog.RemoveAllChildren();
        _kind = catalog[0].String("kind"); _prototype = catalog[0].String("id");
        NativeInterfacePanel.Options(_catalog, catalog.Select(c => (c.String("kind") + ":" + c.String("id"), c.String("name", c.String("id")))), id =>
        { var split = id.Split(':', 2); _kind = split[0]; _prototype = split[1]; });
        var pages = NativeInterfacePanel.Row(); _catalog.AddChild(pages);
        foreach (var delta in new[] { -1, 1 }) NativeInterfacePanel.Button(pages, delta < 0 ? "◀ Каталог" : "Каталог ▶", () => Send("search", new TguiData()
            .String("query", _search.Text).String("category", "all").Number("page", Math.Max(0, data.Int("catalogPage") + delta))));
    }
    private void Apply()
    {
        if (!Dirty || _applying) return;
        _applying = true; _upload = _changes.Values.ToArray(); _uploadId = Guid.NewGuid().ToString("N");
        Send("applyStart", new TguiData().String("id", _uploadId).Number("count", _upload.Length).Number("chunks", Math.Max(1, (_upload.Length + 15) / 16)));
        // The existing handler acknowledges each chunk synchronously to the native view.
        // Chunk zero is sent after the acknowledgement, like the browser transport.
    }
    private void SendChunk(int index) => Send("applyChunk", new TguiData().String("id", _uploadId).Number("index", index).Array("items", _upload.Skip(index * 16).Take(16)));
    private void Refresh()
    {
        _list.RemoveAllChildren();
        var items = _state.Array("items").Where(i => !_changes.ContainsKey(i.String("id")))
            .Concat(_changes.Values.Select(d => NativeUiData.Parse(d.ToString())).Where(i => i.String("kind") != "remove")).ToArray();
        _page = Math.Clamp(_page, 0, Math.Max(0, (items.Length - 1) / 24));
        foreach (var item in items.Skip(_page * 24).Take(24))
        {
            var id = item.String("id"); var row = NativeInterfacePanel.Row(); _list.AddChild(row);
            NativeInterfacePanel.Text(row, $"{item.String("prototype")} ({item.String("x")}, {item.String("y")})");
            NativeInterfacePanel.Button(row, "Удалить", () =>
            {
                if (_applying) return;
                if (id.StartsWith("new_", StringComparison.Ordinal)) _changes.Remove(id);
                else _changes[id] = new TguiData().String("id", id).String("kind", "remove");
                Refresh();
            });
            NativeInterfacePanel.Button(row, "Переместить", () =>
            {
                if (_applying || !Number(_x.Text, out var x) || !Number(_y.Text, out var y) || !Number(_rotation.Text, out var rotation)) return;
                _changes[id] = new TguiData().String("id", id).String("kind", item.String("kind")).String("prototype", item.String("prototype"))
                    .Number("x", x).Number("y", y).Number("rotation", rotation).String("color", item.String("color", "#FFFFFFFF"))
                    .Number("z", item.Int("z")).Bool("anchored", item.Bool("anchored")).Bool("cleanable", item.Bool("cleanable"))
                    .Number("variant", item.Int("variant")).Number("flags", item.Int("flags")).Number("mirror", item.Int("mirror"));
                Refresh();
            });
        }
    }
}
