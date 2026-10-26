using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Maps;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Client._DeepLagoon.Apartments;

/// <summary>Local draft + sprite overlay. Mouse movement never spawns entities or sends network requests.</summary>
public sealed partial class ApartmentEditorSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private Content.Shared.Tag.TagSystem _tags = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    private ApartmentEditorWindow? _window;
    private ApartmentEditorEvent? _state;
    private int _applySequence;
    private static TguiSpriteImages _images => TguiSpriteImages.Shared;
    private ApartmentPreviewOverlay? _overlay;
    private readonly Dictionary<EntityUid, bool> _hidden = new();
    private TimeSpan _openedAt;
    private bool _enteredPreview;
    public List<ApartmentPlacement> Draft { get; private set; } = new();
    public ApartmentTemplatePrototype? Template { get; private set; }
    public ApartmentRoomScene? RoomScene => _state?.Scene;
    public IEnumerable<ApartmentPlacement> MapBaseline => _state?.MapFurniture.Select(item => item.Baseline) ?? Enumerable.Empty<ApartmentPlacement>();
    public ApartmentFurniturePrototype? Catalog(string id) => _state?.MapFurniture.FirstOrDefault(item => item.Baseline.Furniture == id)?.Definition()
        ?? (_prototypes.TryIndex<ApartmentFurniturePrototype>(id, out var item) ? item : null);
    public EntityUid? Grid => _state == null ? null : GetEntity(_state.Grid);
    public bool Lobby => _state?.Lobby ?? false;
    public bool ShowDraft { get; private set; } = true;
    public bool IsOpen => _window != null;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ApartmentEditorEvent>(OnEditor);
        SubscribeNetworkEvent<ApartmentCommerceStateEvent>(OnCommerce);
        SubscribeNetworkEvent<ApartmentResultEvent>(OnResult);
        _overlay = new ApartmentPreviewOverlay(this, EntityManager);
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        CloseEditor(false);
        if (_overlay != null) _overlays.RemoveOverlay(_overlay);
        base.Shutdown();
    }

    private void OnEditor(ApartmentEditorEvent ev)
    {
        if (!_prototypes.TryIndex<ApartmentTemplatePrototype>(ev.Template, out var template)) return;
        RestoreSprites();
        _state = ev;
        _commerce = _commerceResult = null;
        _uploadLayout.Clear(); _uploadId = Guid.Empty;
        _openedAt = _timing.RealTime;
        _enteredPreview = false;
        Template = template;
        Draft = ev.Layout.Select(p => p.Copy()).ToList();
        ShowDraft = true;
        if (_window == null)
        {
            _window = new ApartmentEditorWindow();
            _window.OnClose += () => CloseEditor(true);
            _window.Panel.OnClose += () => CloseEditor(true);
            _window.Panel.OnAction += OnAction;
            _window.OpenCentered();
        }
        Publish();
        RequestCommerce();
        HideSprites();
    }

    private void Publish()
    {
        if (_window == null || _state == null || Template == null) return;
        var data = new TguiData().String("template", Template.ID).String("name", Loc.GetString(Template.Name))
            .Number("number", _state.Number).Bool("lobby", _state.Lobby)
            .Number("revision", _state.Revision).String("token", _state.Token.ToString())
            .Number("width", _state.Scene?.Width ?? Template.Width).Number("height", _state.Scene?.Height ?? Template.Height)
            .Number("maxFurniture", ApartmentLayout.SafetyLimit).Number("arrivalX", Template.Arrival.X).Number("arrivalY", Template.Arrival.Y)
            .Array("reservedCells", Template.ReservedCells.Select(cell => new TguiData().Number("x", cell.X).Number("y", cell.Y)))
            .Array("layout", _state.Layout.Select(PlacementData))
            .Array("catalog", Template.Catalog.Select(id => Catalog(id)!).Where(item => item.Price >= 0).Concat(_state.MapFurniture.Select(item => item.Definition())).Select(item =>
            {
                var mapped = _state.MapFurniture.FirstOrDefault(entry => entry.Baseline.Furniture == item.ID);
                var entry = new TguiData().String("id", item.ID).String("name", item.ExistingOnly ? item.Name : Loc.GetString(item.Name))
                    .Number("width", item.Size.X).Number("height", item.Size.Y).Number("maxCount", ProductStock(item)).String("product", item.StockKey).String("category", item.Category)
                    .String("layer", item.Layer).Bool("blocksMovement", item.BlocksMovement).Bool("existingOnly", item.ExistingOnly).Bool("allowsOverlap", item.AllowsOverlap).Bool("canRotate", item.CanRotate);
                if (mapped != null) entry.String("placementId", mapped.Baseline.Id).Number("originalX", mapped.Baseline.X).Number("originalY", mapped.Baseline.Y).Number("originalRotation", mapped.Baseline.Rotation);
                var prototype = _prototypes.Index<EntityPrototype>(item.Entity);
                prototype.TryGetComponent<SpriteComponent>(out var sprite);
                var images = _state.Entities.TryGetValue(mapped?.Baseline.Id ?? "", out var net)
                    ? _images.MapEntity(GetEntity(net)).ToList() : new List<TguiData>();
                entry.Array("images", images.Count > 0 ? images : _images.MapItem(item.Entity))
                    .Bool("noRotation", sprite?.NoRotation ?? false).Number("drawDepth", sprite?.DrawDepth ?? 0);
                return entry;
            }));
        if (_state.Scene is { } scene)
        {
            var walls = new TguiWallImages(_images, scene.Objects, _prototypes);
            data.Object("scene", new TguiData().Number("x", scene.X).Number("y", scene.Y)
                .Array("walls", scene.Walls.Select(cell => new TguiData().Number("x", cell.X).Number("y", cell.Y)))
                .Array("tiles", scene.Tiles.Select(tile => new TguiData().Number("x", tile.X).Number("y", tile.Y)))
                .Array("objects", scene.Objects.Select(item =>
                {
                    var prototype = _prototypes.Index<EntityPrototype>(item.Prototype);
                    var wall = prototype.TryGetComponent<Content.Shared.Tag.TagComponent>(out var tags) && (_tags.HasTag(tags, "Wall") || _tags.HasTag(tags, "Window"));
                    prototype.TryGetComponent<SpriteComponent>(out var sprite);
                    var entry = new TguiData().String("id", item.Id).String("prototype", item.Prototype)
                        .String("name", prototype.Name).Number("x", item.X).Number("y", item.Y).Bool("wall", wall)
                        .Bool("door", prototype.TryGetComponent<Content.Shared.Pinpointer.NavMapDoorComponent>(out _))
                        .Number("rotation", item.Rotation).Bool("noRotation", sprite?.NoRotation ?? false);
                    if (wall) entry.Array("images", walls.Images(item));
                    else if (!prototype.TryGetComponent<Content.Shared.Pinpointer.NavMapDoorComponent>(out _)) entry.Array("images", _images.MapItem(item.Prototype));
                    return entry;
                }))
                .Array("occupied", scene.Occupied.Select(cell => new TguiData().Number("x", cell.X).Number("y", cell.Y)))
                .Array("blocked", scene.Blocked.Select(cell => new TguiData().Number("x", cell.X).Number("y", cell.Y))));
        }
        AddCommerceData(data);
        _window.Panel.SetState("ApartmentEditor", data.ToString(), "Обустройство квартиры");
    }

    private static TguiData PlacementData(ApartmentPlacement p) => new TguiData().String("id", p.Id)
        .String("furniture", p.Furniture).Number("x", p.X).Number("y", p.Y).Number("rotation", p.Rotation);

    private void OnResult(ApartmentResultEvent ev)
    {
        if (_window is { } window)
            window.Panel.Web.Send("update", new TguiData().Object("data", new TguiData().Number("applySequence", ++_applySequence).String("status", ev.Message)
                .Bool("success", ev.Success)).ToString());
        if (ev.Revision < 0) CloseEditor(false);
    }

    private void OnAction(string action, string payload)
    {
        if (_state == null) return;
        if (action == "layoutChunk") { ReceiveLayoutChunk(payload); return; }
        if (action == "buyFurniture") { PurchaseFurniture(payload); return; }
        if (action == "refreshCommerce") { RequestCommerce(); return; }
        if (action == "shop") { EntityManager.System<ApartmentShopSystem>().Open(); return; }
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (action == "exit" && Lobby) { CloseEditor(true); return; }
        if (action == "exit") { RaiseNetworkEvent(new ApartmentActionEvent("exit")); return; }
        if (action == "close") { CloseEditor(true); return; }
        if (action == "before") { ShowDraft = false; RestoreSprites(); return; }
        if (action == "after") { ShowDraft = true; HideSprites(); return; }
        if (action is not ("preview" or "apply") || !TryParseLayout(payload, out var layout, ApartmentLayout.SafetyLimit)) return;
        Draft = layout;
        if (action == "apply") RaiseNetworkEvent(new ApartmentApplyEvent(_state.Token, _state.Revision, layout));
    }

    /// <summary>Parse bounded JSON through the sandbox-approved YAML reader, as other TGUI panels do.</summary>
    public static bool TryParseLayout(string payload, out List<ApartmentPlacement> layout, int maxCount = ApartmentLayout.SafetyLimit)
    {
        layout = new();
        if (payload.Length > Math.Max(8192, maxCount * 256) || !payload.TrimStart().StartsWith('{')) return false;
        try
        {
            using var reader = new StringReader(payload);
            var yaml = new YamlStream(); yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode.ToDataNode() is not MappingDataNode root ||
                !root.TryGet<SequenceDataNode>("layout", out var sequence) || sequence.Count > maxCount) return false;
            foreach (var child in sequence)
            {
                if (child is not MappingDataNode map) return false;
                string? Field(string key) => map.TryGet<ValueDataNode>(key, out var value) ? value.Value : null;
                if (Field("id") is not { Length: > 0 and <= 64 } id || Field("furniture") is not { Length: > 0 and <= 64 } furniture ||
                    !int.TryParse(Field("x"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                    !int.TryParse(Field("y"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
                    !int.TryParse(Field("rotation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rotation)) return false;
                layout.Add(new ApartmentPlacement { Id = id, Furniture = furniture, X = x, Y = y, Rotation = rotation });
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private void HideSprites()
    {
        if (_state == null || _state.Lobby || !ShowDraft) return;
        foreach (var net in _state.Entities.Values)
        {
            var uid = GetEntity(net);
            if (!TryComp<SpriteComponent>(uid, out var sprite) || _hidden.ContainsKey(uid)) continue;
            _hidden[uid] = sprite.Visible;
            sprite.Visible = false;
        }
    }

    private void RestoreSprites()
    {
        foreach (var (uid, visible) in _hidden)
            if (TryComp<SpriteComponent>(uid, out var sprite)) sprite.Visible = visible;
        _hidden.Clear();
    }

    private void CloseEditor(bool notify)
    {
        RestoreSprites();
        var window = _window;
        _window = null;
        _uploadLayout.Clear(); _uploadId = Guid.Empty;
        _state = null; _commerce = _commerceResult = null; _commerceAwaiting = false; Template = null; Draft.Clear();
        if (notify && window != null) RaiseNetworkEvent(new ApartmentActionEvent("close"));
        if (window == null) return;
        window.Panel.Web.ReleaseTextInput();
        window.Close();
        // GameWebView keeps only the CEF control alive while its browser closes.
#pragma warning disable CS0618
        if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_window == null || Lobby) return;
        if (_player.LocalEntity is not { } body || !TryComp(body, out TransformComponent? transform) || transform.GridUid != Grid)
        {
            // The editor event may arrive before the new grid's first PVS state.
            if (_enteredPreview || _timing.RealTime - _openedAt > TimeSpan.FromSeconds(5)) CloseEditor(true);
            return;
        }
        _enteredPreview = true;
        HideSprites();
    }
}

public sealed class ApartmentEditorWindow : FancyWindow
{
    public override void Close() { if (Panel.CanCloseNative()) base.Close(); }
    public readonly TguiPanel Panel = new();
    public ApartmentEditorWindow()
    {
        Title = "Обустройство квартиры";
        SetSize = new Vector2(1140, 760);
        MinSize = new Vector2(620, 440);
        ContentsContainer.AddChild(Panel);
    }
}
