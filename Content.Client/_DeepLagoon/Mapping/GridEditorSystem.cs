using System.Globalization;
using System.IO;
using System.Text;
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
using Robust.Shared.Timing;
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
    public GridEditorStateEvent? State => _tabs.GetValueOrDefault(_activeTab)?.State;
    private sealed record Entry(string Id, string Kind, string Category, string Name, bool Anchored);
    private List<Entry>? _catalog;
    private int _resultVersion;
    private int _closeVersion;
    [Dependency] private IInputManager _input = default!;
    private bool _editingText;

    public override void Initialize()
    {
        base.Initialize();
        _input.FirstChanceOnKeyEvent += EditorKey;
        SubscribeNetworkEvent<GridEditorStateEvent>(Open);
        SubscribeNetworkEvent<GridEditorCopyResult>(ev => InTab(ev.Token, () =>
        {
            if (ev.Name.Length > 0)
            {
                try { SaveTemplate(ev.Name, ev.Items); }
                catch (Exception e) { Status($"Не удалось сохранить шаблон: {e.Message}", false); return; }
            }
            else SendTabUpdate(new TguiData().Object("data", new TguiData().String("copyVersion", ev.Request)
                .Array("copyItems", ev.Items.Select(ItemData))).ToString());
            Status(ev.Errors.Length > 0 ? ev.Errors : "Копирование завершено.", ev.Errors.Length == 0);
        }));
        SubscribeNetworkEvent<GridEditorPatchEvent>(ev => InTab(ev.Token, () => Patch(ev)));
        SubscribeNetworkEvent<GridEditorNameResult>(ev => InTab(ev.Token, () => NameResult(ev)));
        SubscribeNetworkEvent<GridEditorRepairConnectionsEvent>(RepairConnections);
        SubscribeNetworkEvent<GridEditorAtmosResult>(ev => InTab(ev.Token, () => AtmosResult(ev)));
        SubscribeNetworkEvent<GridEditorEnvironmentState>(ev => InTab(ev.Token, () => EnvironmentState(ev)));
        SubscribeNetworkEvent<GridEditorDraftsEvent>(ev => InTab(ev.Token, () => PeerDrafts(ev)));
        SubscribeNetworkEvent<GridEditorResultEvent>(ev => InTab(ev.Token, () => SendTabUpdate(
            new TguiData().Object("data", new TguiData().String("status", ev.Message).Bool("success", ev.Success)
                .Number("resultVersion", ++_resultVersion)).ToString())));
        SubscribeNetworkEvent<GridEditorFileEvent>(ev =>
        {
            var tab = _tabs.FirstOrDefault(pair => pair.Value.DownloadRequest == ev.Request);
            if (tab.Value != null) InTab(tab.Key, () => ReceiveFile(ev));
        });
        SubscribeNetworkEvent<GridEditorClosedEvent>(ev => CloseTab(ev.Token, false));
        SubscribeNetworkEvent<GridEditorDraftFileChunk>(ev => InTab(ev.Token, () => ReceiveDraftFileAck(ev)));
    }

    public override void Shutdown() { _input.FirstChanceOnKeyEvent -= EditorKey; Close(true); base.Shutdown(); }

    private void EditorKey(KeyEventArgs args, KeyEventType type)
    {
        if (args.Handled || _editingText || _window is not { VisibleInTree: true } window || !window.IsAtFront() ||
            !window.Panel.Web.HasInputFocus || !window.Panel.Web.IsReady) return;
        var code = args.Key switch
        {
            Keyboard.Key.Z => "KeyZ", Keyboard.Key.Y => "KeyY", Keyboard.Key.C => "KeyC",
            Keyboard.Key.V => "KeyV", Keyboard.Key.S => "KeyS",
            Keyboard.Key.Num1 => "Digit1", Keyboard.Key.Num2 => "Digit2", Keyboard.Key.Num3 => "Digit3", Keyboard.Key.Num4 => "Digit4",
            Keyboard.Key.E => "KeyE", Keyboard.Key.R => "KeyR", Keyboard.Key.Space => "Space",
            Keyboard.Key.Shift => "ShiftLeft", Keyboard.Key.Delete => "Delete", Keyboard.Key.Escape => "Escape",
            Keyboard.Key.Left => "ArrowLeft", Keyboard.Key.Right => "ArrowRight",
            Keyboard.Key.D => "KeyD", Keyboard.Key.M => "KeyM", Keyboard.Key.Return => "Enter",
            Keyboard.Key.Up => "ArrowUp", Keyboard.Key.Down => "ArrowDown",
            _ => null
        };
        if (code == null || args.Alt || args.Control && code is not ("KeyZ" or "KeyY" or "KeyC" or "KeyV" or "KeyS")) return;
        // Native CEF paste carries clipboard images. The editor handles its paste event,
        // falling back to its internal building clipboard if no image is present.
        if (args.Control && code == "KeyV") return;
        args.Handle();
        if (type == KeyEventType.Repeat && !code.StartsWith("Arrow", StringComparison.Ordinal)) return;
        window.Panel.Web.SendGridEditorKey(code, type != KeyEventType.Up, args.Control, args.Shift, args.Alt);
    }

    private void Open(GridEditorStateEvent ev)
    {
        if (_tabs.ContainsKey(ev.Token)) { ActivateTab(ev.Token); return; }
        _tab = new EditorTab { State = ev, OpenOrder = ++_nextTabOrder };
        _tabs.Add(ev.Token, _tab);
        if (_window == null)
        {
            _window = new GridEditorWindow();
            _window.OnClose += Close;
            _window.CloseRequested += () => InTab(_activeTab, () => SendTabUpdate(new TguiData()
                .Object("data", new TguiData().Number("closeRequested", ++_closeVersion)).ToString()));
            _window.Panel.OnAction += TabAction;
            _window.Panel.OnClose += Close;
            _window.Panel.Web.Ready += ReplayEditor;
            _window.Panel.Web.Resumed += ReplayEditor;
            _window.Panel.SetState("GridEditor", "{}", "Редактор grid");
            _window.OpenCentered();
            _window.Panel.Web.FocusInput();
        }
        PublishCurrentState();
        LoadLibrary();
        ActivateTab(ev.Token);
    }

    private void PublishCurrentState()
    {
        if (_window?.Panel.Web.IsReady != true || _tab.State is not { } ev) return;
        var visuals = ev.Items.Concat(_tab.RecoveryEdits.Values).Where(i => i.Kind != "remove").Select(i => (i.Kind, i.Prototype))
            .Append(("tile", "Plating")).Distinct().Select(i => Visual(i.Item1, i.Item2));
        var header = new TguiData().String("grid", ev.Grid.ToString()).String("token", ev.Token.ToString()).String("sourcePath", ev.SourcePath)
            .String("gridName", _tab.EnvironmentState?.GridName ?? "").Number("gridNameMaxLength", GridEditorNameRequest.MaxLength)
            .String("gridNameError", "").Number("gridNameResultVersion", _nameResultVersion)
            .Number("x", ev.X).Number("y", ev.Y).Number("width", ev.Width).Number("height", ev.Height)
            .Number("gridX", ev.GridX).Number("gridY", ev.GridY).Number("gridWidth", ev.GridWidth).Number("gridHeight", ev.GridHeight)
            .Bool("alwaysPowered", _tab.EnvironmentState?.AlwaysPowered ?? false).Bool("forceGravity", _tab.EnvironmentState?.ForceGravity ?? false)
            .Bool("safeAtmosphere", _tab.EnvironmentState?.SafeAtmosphere ?? false).Bool("apartmentProtected", _tab.EnvironmentState?.ApartmentProtected ?? false)
            .Array("exportIssues", Array.Empty<TguiData>())
            .Number("previewGeneration", _tab.PreviewGeneration).Number("snapshotPatchVersion", _tab.PatchVersion)
            .Number("lastAppliedVersion", _tab.LastAppliedVersion).Bool("initialized", ev.Initialized).Bool("copySupported", true)
            .Array("routeDevices", RouteDevices()).Array("visuals", visuals);
        var recovery = new TguiData().Array("edits", builder => GridEditorJson.AppendItems(builder, _tab.RecoveryEdits.Values))
            .Object("expansion", new TguiData().Number("left", _tab.RecoveryExpansion.Left).Number("right", _tab.RecoveryExpansion.Right)
                .Number("bottom", _tab.RecoveryExpansion.Bottom).Number("top", _tab.RecoveryExpansion.Top)).ToString();
        _tab.Snapshot = new SnapshotTransfer(header, ev.Items, recovery);
        _snapshotQueue.Enqueue(ev.Token);
    }

    private void EnvironmentState(GridEditorEnvironmentState state)
    {
        if (_tab.State == null || _tab.State.Token != state.Token) return;
        _tab.EnvironmentState = state;
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .String("gridName", state.GridName).Bool("alwaysPowered", state.AlwaysPowered).Bool("forceGravity", state.ForceGravity)
            .Bool("safeAtmosphere", state.SafeAtmosphere).Bool("apartmentProtected", state.ApartmentProtected)).ToString());
    }

    private int _atmosVersion;
    private int _nameResultVersion;
    private void NameResult(GridEditorNameResult result)
    {
        if (_tab.State == null || _tab.State.Token != result.Token) return;
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .Number("gridNameResultVersion", ++_nameResultVersion).String("gridNameError", result.Error)).ToString());
    }

    private void AtmosResult(GridEditorAtmosResult ev)
    {
        if (_tab.State == null || _tab.State.Token != ev.Token) return;
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .Number("atmosPatchVersion", _tab.PatchVersion).Number("atmosVersion", ++_atmosVersion).String("atmosToken", ev.Token.ToString()).Number("atmosFilled", ev.Filled)
            .Number("atmosSealed", ev.Cells.Count(i => i.Status == GridEditorAirStatus.Sealed))
            .Number("atmosLeaking", ev.Cells.Count(i => i.Status == GridEditorAirStatus.Leaking))
            .Array("atmosCells", ev.Cells.Select(i => new TguiData().Number("x", i.X).Number("y", i.Y)
                .Number("region", i.Region).Number("leaks", (int) i.Leaks).Number("blocked", (int) i.Blocked)
                .String("status", i.Status.ToString().ToLowerInvariant()).Number("pressure", i.Pressure)))).ToString());
    }

    private void Patch(GridEditorPatchEvent ev)
    {
        if (_tab.State == null || _tab.State.Token != ev.Token) return;
        RebaseRecovery(ev);
        var items = _tab.State.Items.ToDictionary(i => i.Id);
        foreach (var id in ev.Removed) items.Remove(id);
        foreach (var item in ev.Items) items[item.Id] = item;
        _tab.State = new GridEditorStateEvent(_tab.State.Grid, ev.Token, ev.X, ev.Y, ev.Width, ev.Height,
            items.Values.ToList(), _tab.State.Initialized, ev.GridX, ev.GridY, ev.GridWidth, ev.GridHeight, _tab.State.SourcePath);
        _tab.PreviewGeneration = ev.PreviewGeneration;
        _tab.PendingPatches.Add((++_tab.PatchVersion, ev));
        PublishPendingPatches();
    }

    private void PublishPendingPatches()
    {
        if (_tab.Snapshot != null || _tab.PendingPatches.Count == 0) return;
        var ev = _tab.PendingPatches[^1].Patch;
        SendTabUpdate(new TguiData().Object("data", new TguiData()
            .Number("x", ev.X).Number("y", ev.Y).Number("width", ev.Width).Number("height", ev.Height)
            .Number("gridX", ev.GridX).Number("gridY", ev.GridY).Number("gridWidth", ev.GridWidth).Number("gridHeight", ev.GridHeight)
            .Number("previewGeneration", _tab.PreviewGeneration).Number("patchVersion", _tab.PatchVersion).Bool("patchApplied", ev.Applied)
            .Array("patchBatches", _tab.PendingPatches.Select(entry => new TguiData().Number("version", entry.Version)
                .Bool("applied", entry.Patch.Applied).Number("x", entry.Patch.X).Number("y", entry.Patch.Y)
                .Number("width", entry.Patch.Width).Number("height", entry.Patch.Height)
                .Array("superseded", entry.Patch.Superseded.Select(ItemData))
                .Array("failedItems", entry.Patch.FailedItems.Select(ItemData))
                .Array("items", entry.Patch.Items.Select(ItemData))
                .Array("removed", entry.Patch.Removed.Select(id => new TguiData().String("id", id)))))
            .Array("patchItems", ev.Items.Select(ItemData))
            .Array("patchRemoved", ev.Removed.Select(id => new TguiData().String("id", id)))
            .Array("patchVisuals", _tab.PendingPatches.SelectMany(entry => entry.Patch.Items).Select(i => (i.Kind, i.Prototype)).Distinct().Select(i => Visual(i.Kind, i.Prototype))))
            .ToString());
    }

    private TguiData Visual(string kind, string id)
    {
        var data = new TguiData().String("id", id).String("kind", kind);
        if (kind == "entity")
        {
            if (!_prototypes.TryIndex<EntityPrototype>(id, out var proto)) return data.String("name", id).Array("images", Array.Empty<TguiData>());
            return data.Array("images", _images.MapItem(id)).Bool("noRotation", proto.TryGetComponent<SpriteComponent>(out var mappedSprite) && mappedSprite.NoRotation).String("name", proto.Name)
                .Bool("pipeColor", proto.Components.ContainsKey("AtmosPipeColor"))
                .Number("drawDepth", proto.TryGetComponent<SpriteComponent>(out var sprite) ? sprite.DrawDepth : 0)
                .Bool("exclusive", GridEditorPlacement.IsExclusive(proto, _tags)).Bool("diagonal", GridEditorPlacement.HasTag(proto, "Diagonal", _tags))
                .String("category", GridEditorPlacement.IsWall(proto, _tags) ? "wall" : "entity");
        }
        var spec = kind == "decal" ? _prototypes.Index<DecalPrototype>(id).Sprite :
            _prototypes.Index<ContentTileDefinition>(id).Sprite is { } path ? new SpriteSpecifier.Texture(path) : null;
        var size = kind == "decal" && spec != null ? EntityManager.System<SpriteSystem>().Frame0(spec).Size : new Vector2i(32, 32);
        return data.Bool("allowRotationMirror", kind == "tile" && _prototypes.Index<ContentTileDefinition>(id).AllowRotationMirror)
            .Array("images", spec == null ? Array.Empty<TguiData>() :
            new[] { new TguiData().String("url", kind == "tile" && spec is SpriteSpecifier.Texture texture ?
                _images.TileFrame(texture.TexturePath) : _images.Frame(spec)).String("color", "#ffffff").Number("width", size.X).Number("height", size.Y) });
    }

    private static TguiData ItemData(GridEditorItem i)
    {
        var data = new TguiData().String("id", i.Id).String("kind", i.Kind)
        .String("prototype", i.Prototype).Number("x", i.X).Number("y", i.Y).Number("rotation", i.Rotation)
        .String("color", i.Color).Number("z", i.Z).Bool("cleanable", i.Cleanable).Bool("anchored", i.Anchored)
        .Number("variant", i.Variant).Number("flags", i.Flags).Number("mirror", i.Mirror)
        .Number("pipeLayer", i.PipeLayer).String("entityData", i.EntityData).String("entityGroup", i.EntityGroup);
        if (i.GasPorts != 0) data.Number("gasPorts", i.GasPorts);
        return data;
    }

    private void Search(string query, string category, int page)
    {
        if (_window == null) return;
        _tab.SearchQuery = query; _tab.SearchCategory = category;
        _catalog ??= _prototypes.EnumeratePrototypes<EntityPrototype>().Where(p => !p.Abstract && !p.HideSpawnMenu)
            .Select(p => new Entry(p.ID, "entity", p.TryGetComponent<TagComponent>(out var tag) && _tags.HasTag(tag, "Wall") ? "wall" : "entity",
                p.Name, p.Components.ContainsKey("Anchorable") ||
                    p.TryGetComponent<TransformComponent>(out var transform) && transform.Anchored))
            .Concat(_prototypes.EnumeratePrototypes<ContentTileDefinition>().Where(p => !p.Abstract && p.TileId != 0)
                .Select(p => new Entry(p.ID, "tile", "tile", Loc.GetString(p.Name), false)))
            .Concat(_prototypes.EnumeratePrototypes<DecalPrototype>().Select(p => new Entry(p.ID, "decal", "decal", p.ID, false)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var result = _catalog.Where(p => (category == "all" || p.Category == category || category == "favorites" && _favorites.ContainsKey(p.Kind + ":" + p.Id)) &&
            (p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (result.Count - 1) / 48));
        _tab.SearchPage = page;
        SendTabUpdate(new TguiData().Object("data", new TguiData().Number("catalogCount", result.Count)
            .String("catalogQuery", query).String("catalogCategory", category).Number("catalogPage", page).Array("catalog", result.Skip(page * 48).Take(48).Select(p =>
                Visual(p.Kind, p.Id).String("name", p.Name).String("category", p.Category).Bool("anchored", p.Anchored)))).ToString());
    }

    private void Action(string action, string payload)
    {
        if (action == "copySelection") { IoCManager.Resolve<Robust.Client.UserInterface.IClipboardManager>().SetText("DeepLagoon grid editor selection"); return; }
        if (action == "referenceImport") { ImportReference(); return; }
        if (action == "close") { if (_tab.State is { } state) CloseTab(state.Token, true); return; }
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if ((_tab.State == null && action is not ("draftFileStart" or "draftFileChunk")) || payload.Length > 4_000_000) return;
        try
        {
            using var reader = new StringReader(payload);
            var yaml = new YamlStream(); yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode.ToDataNode() is not MappingDataNode root) return;
            if (DraftFileAction(action, root)) return;
            if (_tab.State == null) return;
            if (action == "repairConnections")
            {
                RaiseNetworkEvent(new GridEditorRepairConnectionsRequest(_tab.State.Token));
                return;
            }
            static string Field(MappingDataNode node, string key, string fallback = "") =>
                node.TryGet<ValueDataNode>(key, out var value) ? value.Value : fallback;
            if (action == "editing") { _editingText = Field(root, "active") == "true"; return; }
            if (PreviewAction(action, root)) return;
            if (action == "patchAck")
            {
                if (int.TryParse(Field(root, "version"), out var version) && version <= _tab.PatchVersion)
                    _tab.PendingPatches.RemoveAll(entry => entry.Version <= version);
                return;
            }
            if (action is "setGridPower" or "setGridGravity")
            {
                RaiseNetworkEvent(new GridEditorEnvironmentRequest(_tab.State.Token,
                    action == "setGridPower" ? "power" : "gravity", Field(root, "enabled") == "true"));
                return;
            }
            if (action == "setGridName")
            {
                RaiseNetworkEvent(new GridEditorNameRequest(_tab.State.Token, Field(root, "name")));
                return;
            }
            if (action is "diagnoseAtmos" or "fillAtmos")
            {
                RaiseNetworkEvent(new GridEditorAtmosRequest(_tab.State.Token, action == "fillAtmos")); return;
            }
            if (action == "region")
            {
                if (int.TryParse(Field(root, "x"), out var x) && int.TryParse(Field(root, "y"), out var y))
                    RaiseNetworkEvent(new GridEditorRegionEvent(_tab.State.Token, x, y));
                return;
            }
            if (action == "download") { BeginDownload(Field(root, "scope") == "map"); return; }
            if (action == "routeVisuals")
            {
                if (!root.TryGet<SequenceDataNode>("prototypes", out var entries) || entries.Count > 32) return;
                var ids = entries.OfType<ValueDataNode>().Select(value => value.Value).Where(id =>
                    _prototypes.TryIndex<EntityPrototype>(id, out var proto) && !proto.Abstract).Distinct();
                SendTabUpdate(new TguiData().Object("data", new TguiData()
                    .Array("routeVisuals", ids.Select(id => Visual("entity", id)))).ToString());
                return;
            }
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
                var encoded = Field(root, "encoding") == "json";
                if (id.Length is < 1 or > 64 || !int.TryParse(Field(root, "count"), out var count) || count < 0 ||
                    !int.TryParse(Field(root, "chunks"), out var chunks) || (encoded ? chunks is < 1 or > 32768 : chunks != Math.Max(1, (count + 15L) / 16))) return;
                _tab.UploadPurpose = Field(root, "purpose", "apply"); _tab.UploadName = Field(root, "name");
                _tab.UploadExpansion = null;
                if (_tab.UploadPurpose == "apply" && root.TryGet<MappingDataNode>("expansion", out var expansion))
                {
                    int Side(string key) => int.Parse(Field(expansion, key, "0"), CultureInfo.InvariantCulture);
                    _tab.UploadExpansion = new GridEditorExpansion { Left = Side("left"), Right = Side("right"),
                        Bottom = Side("bottom"), Top = Side("top") };
                }
                if (_tab.UploadPurpose is not ("apply" or "template" or "copy") || _tab.UploadPurpose == "template" &&
                    (count is < 1 or > 512 || _tab.UploadName.Length is < 1 or > 64)) return;
                _tab.UploadId = id; _tab.UploadCount = count; _tab.UploadChunks = chunks; _tab.UploadNext = 0; _tab.Upload.Clear();
                _tab.UploadText = encoded ? new StringBuilder() : null;
                AckUpload();
                return;
            }
            if (action != "applyChunk" || _tab.UploadId == null || Field(root, "id") != _tab.UploadId ||
                !int.TryParse(Field(root, "index"), out var index) || index != _tab.UploadNext) return;
            if (_tab.UploadText is { } text)
            {
                var part = Field(root, "text");
                if (part.Length is < 1 or > 1024 || text.Length + part.Length > 32 * 1024 * 1024) throw new InvalidDataException("Некорректная часть сцены.");
                text.Append(part);
                if (_tab.UploadNext + 1 == _tab.UploadChunks) _tab.Upload.AddRange(ReadItems(ReadItemText(text)));
            }
            else
            {
                if (!root.TryGet<SequenceDataNode>("items", out var sequence) || sequence.Count > 16) return;
                _tab.Upload.AddRange(ReadItems(sequence));
            }
            _tab.UploadNext++;
            if (_tab.Upload.Count > _tab.UploadCount) throw new InvalidDataException("Upload overflow");
            if (_tab.UploadNext < _tab.UploadChunks) { AckUpload(); return; }
            if (_tab.Upload.Count != _tab.UploadCount) throw new InvalidDataException("Incomplete upload");
            if (_tab.UploadPurpose is "template" or "copy") RaiseNetworkEvent(new GridEditorCopyRequest(_tab.State.Token,
                _tab.UploadId, new List<GridEditorItem>(_tab.Upload), _tab.UploadPurpose == "template" ? _tab.UploadName : ""));
            else
            {
                RaiseNetworkEvent(new GridEditorApplyEvent(_tab.State.Token, new List<GridEditorItem>(_tab.Upload), _tab.UploadExpansion, patch: true));
            }
            _tab.UploadId = null; _tab.Upload.Clear(); _tab.UploadText = null;
        }
        catch (Exception e)
        {
            SendTabUpdate(new TguiData().Object("data", new TguiData().String("status", $"Действие не выполнено: {e.Message}")
                .Bool("success", false).Number("resultVersion", ++_resultVersion)).ToString());
            _tab.UploadId = null; _tab.Upload.Clear(); _tab.UploadText = null;
        }
    }

    // Keep truncation and range validation without conv.ovf.u1, unsupported by the engine sandbox.
    private static byte CheckedByte(float value)
    {
        var integer = checked((int) value);
        if (integer is < byte.MinValue or > byte.MaxValue) throw new InvalidDataException("Byte value outside 0–255 range");
        return unchecked((byte) integer);
    }

    private void AckUpload() => SendTabUpdate(new TguiData().Object("data",
        new TguiData().String("uploadId", _tab.UploadId).Number("uploadNext", _tab.UploadNext)).ToString());

    private void Close() => Close(false);

    private void Close(bool shuttingDown)
    {
        _snapshotQueue.Clear();
        var window = _window;
        foreach (var token in _tabs.Keys.ToArray()) InTab(token, CancelDownload);
        _tabs.Clear();
        _window = null; _tab = new EditorTab();
        _editingText = false;
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
    public override void Close()
    {
        if (Panel.UsingNative) { if (Panel.CanCloseNative()) ForceClose(); }
        else CloseRequested?.Invoke();
    }
    public void ForceClose() => base.Close();
    public GridEditorWindow()
    {
        Title = "Редактор grid";
        SetSize = new Vector2(1180, 760);
        MinSize = new Vector2(760, 520);
        ContentsContainer.AddChild(Panel);
    }
}
