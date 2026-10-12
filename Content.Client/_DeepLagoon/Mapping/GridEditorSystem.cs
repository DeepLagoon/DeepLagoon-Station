using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Decals;
using Content.Shared.Maps;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using Robust.Shared.Console;
using Robust.Client.Input;
using Robust.Client.GameObjects;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private TagSystem _tags = default!;
    private static TguiSpriteImages _images => TguiSpriteImages.Shared;
    private GridEditorWindow? _window;
    private GridEditorStateEvent? _state;
    public GridEditorStateEvent? State => _state;
    private sealed record Entry(string Id, string Kind, string Category, string Name, bool Anchored);
    private List<Entry>? _catalog;
    private int _resultVersion;
    private int _closeVersion;
    [Dependency] private IInputManager _input = default!;
    private bool _editingText;
    private string _searchQuery = "", _searchCategory = "tile";
    private int _searchPage;
    private string? _uploadId;
    private int _uploadCount, _uploadChunks, _uploadNext;
    private readonly List<GridEditorItem> _upload = new();

    public override void Initialize()
    {
        base.Initialize();
        _input.FirstChanceOnKeyEvent += EditorKey;
        SubscribeNetworkEvent<GridEditorStateEvent>(Open);
        SubscribeNetworkEvent<GridEditorResultEvent>((ev) => _window?.Panel.Web.Send("update",
            new TguiData().Object("data", new TguiData().String("status", ev.Message).Bool("success", ev.Success)
                .Number("resultVersion", ++_resultVersion)).ToString()));
        SubscribeNetworkEvent<GridEditorFileEvent>(ReceiveFile);
    }

    public override void Shutdown() { _input.FirstChanceOnKeyEvent -= EditorKey; Close(true); base.Shutdown(); }

    private void EditorKey(KeyEventArgs args, KeyEventType type)
    {
        if (args.Handled || _editingText || _window is not { VisibleInTree: true } window || !window.IsAtFront() ||
            !window.Panel.Web.HasInputFocus || !window.Panel.Web.IsReady) return;
        var code = args.Key switch
        {
            Keyboard.Key.Z => "KeyZ", Keyboard.Key.Y => "KeyY", Keyboard.Key.C => "KeyC",
            Keyboard.Key.V => "KeyV", Keyboard.Key.H => "KeyH", Keyboard.Key.B => "KeyB",
            Keyboard.Key.L => "KeyL", Keyboard.Key.U => "KeyU", Keyboard.Key.E => "KeyE",
            Keyboard.Key.I => "KeyI", Keyboard.Key.R => "KeyR", Keyboard.Key.Space => "Space",
            Keyboard.Key.Shift => "ShiftLeft", Keyboard.Key.Delete => "Delete", Keyboard.Key.Escape => "Escape",
            _ => null
        };
        if (code == null || args.Alt || args.Control && code is not ("KeyZ" or "KeyY" or "KeyC" or "KeyV")) return;
        args.Handle();
        if (type == KeyEventType.Repeat) return;
        window.Panel.Web.SendGridEditorKey(code, type == KeyEventType.Down, args.Control, args.Shift, args.Alt);
    }

    private void Open(GridEditorStateEvent ev)
    {
        _uploadId = null; _upload.Clear();
        _state = ev;
        if (_window == null)
        {
            _window = new GridEditorWindow();
            _window.OnClose += Close;
            _window.CloseRequested += () => _window?.Panel.Web.Send("update", new TguiData()
                .Object("data", new TguiData().Number("closeRequested", ++_closeVersion)).ToString());
            _window.Panel.OnAction += Action;
            _window.Panel.OnClose += Close;
            _window.Panel.Web.Ready += () =>
            {
                PublishLibrary();
                Search(_searchQuery, _searchCategory, _searchPage);
            };
            _window.OpenCentered();
            _window.Panel.Web.FocusInput();
        }
        var visuals = ev.Items.Select(i => (i.Kind, i.Prototype)).Distinct().Select(i => Visual(i.Kind, i.Prototype));
        _window.Panel.SetState("GridEditor", new TguiData().String("grid", ev.Grid.ToString()).String("token", ev.Token.ToString())
            .Number("x", ev.X).Number("y", ev.Y).Number("width", ev.Width).Number("height", ev.Height)
            .Bool("initialized", ev.Initialized).Array("items", ev.Items.Select(ItemData)).Array("visuals", visuals).ToString(), "Редактор grid");
        LoadLibrary(); PublishLibrary();
        Search(_searchQuery, _searchCategory, _searchPage);
    }

    private TguiData Visual(string kind, string id)
    {
        var data = new TguiData().String("id", id).String("kind", kind);
        if (kind == "entity")
        {
            var proto = _prototypes.Index<EntityPrototype>(id);
            return data.Array("images", _images.Item(id)).String("name", proto.Name)
                .Number("drawDepth", proto.TryGetComponent<SpriteComponent>(out var sprite) ? sprite.DrawDepth : 0)
                .String("category", proto.TryGetComponent<TagComponent>(out var tag) && _tags.HasTag(tag, "Wall") ? "wall" : "entity");
        }
        var spec = kind == "decal" ? _prototypes.Index<DecalPrototype>(id).Sprite :
            _prototypes.Index<ContentTileDefinition>(id).Sprite is { } path ? new SpriteSpecifier.Texture(path) : null;
        return data.Array("images", spec == null ? Array.Empty<TguiData>() :
            new[] { new TguiData().String("url", kind == "tile" && spec is SpriteSpecifier.Texture texture ?
                _images.TileFrame(texture.TexturePath) : _images.Frame(spec)).String("color", "#ffffff") });
    }

    private static TguiData ItemData(GridEditorItem i) => new TguiData().String("id", i.Id).String("kind", i.Kind)
        .String("prototype", i.Prototype).Number("x", i.X).Number("y", i.Y).Number("rotation", i.Rotation)
        .String("color", i.Color).Number("z", i.Z).Bool("cleanable", i.Cleanable).Bool("anchored", i.Anchored)
        .Number("variant", i.Variant).Number("flags", i.Flags).Number("mirror", i.Mirror);

    private void Search(string query, string category, int page)
    {
        if (_window == null) return;
        _searchQuery = query; _searchCategory = category;
        _catalog ??= _prototypes.EnumeratePrototypes<EntityPrototype>().Where(p => !p.Abstract && !p.HideSpawnMenu)
            .Select(p => new Entry(p.ID, "entity", p.TryGetComponent<TagComponent>(out var tag) && _tags.HasTag(tag, "Wall") ? "wall" : "entity",
                p.Name, p.Components.ContainsKey("Anchorable")))
            .Concat(_prototypes.EnumeratePrototypes<ContentTileDefinition>().Where(p => !p.Abstract && p.TileId != 0)
                .Select(p => new Entry(p.ID, "tile", "tile", Loc.GetString(p.Name), false)))
            .Concat(_prototypes.EnumeratePrototypes<DecalPrototype>().Select(p => new Entry(p.ID, "decal", "decal", p.ID, false)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var result = _catalog.Where(p => (category == "all" || p.Category == category || category == "favorites" && _favorites.ContainsKey(p.Kind + ":" + p.Id)) &&
            (p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (result.Count - 1) / 48));
        _searchPage = page;
        _window.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().Number("catalogCount", result.Count)
            .String("catalogQuery", query).String("catalogCategory", category).Number("catalogPage", page).Array("catalog", result.Skip(page * 48).Take(48).Select(p =>
                Visual(p.Kind, p.Id).String("name", p.Name).String("category", p.Category).Bool("anchored", p.Anchored)))).ToString());
    }

    private void Action(string action, string payload)
    {
        if (action == "close") { Close(); return; }
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (action == "fixAtmos" && _state != null) { IoCManager.Resolve<IConsoleHost>().ExecuteCommand($"fixgridatmos {_state.Grid}"); return; }
        if (_state == null || payload.Length > 4_000_000) return;
        try
        {
            using var reader = new StringReader(payload);
            var yaml = new YamlStream(); yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode.ToDataNode() is not MappingDataNode root) return;
            static string Field(MappingDataNode node, string key, string fallback = "") =>
                node.TryGet<ValueDataNode>(key, out var value) ? value.Value : fallback;
            if (action == "editing") { _editingText = Field(root, "active") == "true"; return; }
            if (action == "region")
            {
                if (int.TryParse(Field(root, "x"), out var x) && int.TryParse(Field(root, "y"), out var y))
                    RaiseNetworkEvent(new GridEditorRegionEvent(_state.Token, x, y));
                return;
            }
            if (action == "download") { BeginDownload(Field(root, "scope") == "map"); return; }
            if (LibraryAction(action, root)) return;
            if (action == "search")
            {
                var query = Field(root, "query");
                if (query.Length > 100) return;
                int.TryParse(Field(root, "page"), out var page);
                Search(query, Field(root, "category", "all"), page);
                return;
            }
            if (action == "applyStart")
            {
                var id = Field(root, "id");
                if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "count"), out var count) || count is < 0 or > 12000 ||
                    !int.TryParse(Field(root, "chunks"), out var chunks) || chunks != Math.Max(1, (count + 15) / 16)) return;
                _uploadPurpose = Field(root, "purpose", "apply"); _uploadName = Field(root, "name");
                if (_uploadPurpose is not ("apply" or "template") || _uploadPurpose == "template" &&
                    (count is < 1 or > 512 || _uploadName.Length is < 1 or > 64)) return;
                _uploadId = id; _uploadCount = count; _uploadChunks = chunks; _uploadNext = 0; _upload.Clear();
                AckUpload();
                return;
            }
            if (action != "applyChunk" || _uploadId == null || Field(root, "id") != _uploadId ||
                !int.TryParse(Field(root, "index"), out var index) || index != _uploadNext ||
                !root.TryGet<SequenceDataNode>("items", out var sequence) || sequence.Count > 16) return;
            var items = new List<GridEditorItem>();
            foreach (var child in sequence)
            {
                if (child is not MappingDataNode node) return;
                float Number(string key) => float.Parse(Field(node, key, "0"), CultureInfo.InvariantCulture);
                items.Add(new GridEditorItem { Id = Field(node, "id"), Kind = Field(node, "kind"), Prototype = Field(node, "prototype"),
                    X = Number("x"), Y = Number("y"), Rotation = Number("rotation"), Z = (int) Number("z"),
                    Color = Field(node, "color", "#FFFFFFFF"), Cleanable = Field(node, "cleanable") == "true",
                    Anchored = Field(node, "anchored") == "true", Variant = CheckedByte(Number("variant")),
                    Flags = CheckedByte(Number("flags")), Mirror = CheckedByte(Number("mirror")) });
            }
            _upload.AddRange(items);
            _uploadNext++;
            if (_upload.Count > _uploadCount) throw new InvalidDataException("Upload overflow");
            if (_uploadNext < _uploadChunks) { AckUpload(); return; }
            if (_upload.Count != _uploadCount) throw new InvalidDataException("Incomplete upload");
            if (_uploadPurpose == "template") SaveTemplate(_uploadName, new List<GridEditorItem>(_upload));
            else
            {
                var final = _state.Items.ToDictionary(i => i.Id);
                foreach (var item in _upload)
                    if (item.Kind == "remove") final.Remove(item.Id);
                    else final[item.Id] = item;
                RaiseNetworkEvent(new GridEditorApplyEvent(_state.Token, final.Values.ToList()));
            }
            _uploadId = null; _upload.Clear();
        }
        catch (Exception e)
        {
            _window?.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().String("status", $"Действие не выполнено: {e.Message}")
                .Bool("success", false).Number("resultVersion", ++_resultVersion)).ToString());
            _uploadId = null; _upload.Clear();
        }
    }

    // Keep truncation and range validation without conv.ovf.u1, unsupported by the engine sandbox.
    private static byte CheckedByte(float value)
    {
        var integer = checked((int) value);
        if (integer is < byte.MinValue or > byte.MaxValue) throw new InvalidDataException("Byte value outside 0–255 range");
        return unchecked((byte) integer);
    }

    private void AckUpload() => _window?.Panel.Web.Send("update", new TguiData().Object("data",
        new TguiData().String("uploadId", _uploadId).Number("uploadNext", _uploadNext)).ToString());

    private void Close() => Close(false);

    private void Close(bool shuttingDown)
    {
        var window = _window;
        _window = null; _state = null; _uploadId = null; _upload.Clear();
        _editingText = false;
        CancelDownload();
        if (window == null) return;
        if (!shuttingDown) RaiseNetworkEvent(new GridEditorCloseEvent());
        if (shuttingDown) window.Panel.Web.PrepareForShutdown();
        else window.Panel.Web.ReleaseTextInput();
        window.ForceClose();
#pragma warning disable CS0618
        if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
    }
}

public sealed class GridEditorWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(captureKeyboard: true);
    public event Action? CloseRequested;
    public override void Close() => CloseRequested?.Invoke();
    public void ForceClose() => base.Close();
    public GridEditorWindow()
    {
        Title = "Редактор grid";
        SetSize = new Vector2(1180, 760);
        MinSize = new Vector2(760, 520);
        ContentsContainer.AddChild(Panel);
    }
}
