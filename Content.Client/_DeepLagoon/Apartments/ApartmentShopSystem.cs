using System.Linq;
using Robust.Shared.Prototypes;
using Robust.Client.GameObjects;
using Content.Shared.Maps;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Client._DeepLagoon.Apartments;

public sealed class ApartmentShopSystem : EntitySystem
{
    private ApartmentTravelWindow? _window;
    private ApartmentShopState? _state;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private TguiData? _preview, _gallery;
    private int _galleryRequest;
    public override void Initialize()
    {
        SubscribeNetworkEvent<ApartmentShopState>(OnState);
        SubscribeNetworkEvent<ApartmentShopPreview>(OnPreview);
        SubscribeNetworkEvent<ApartmentGalleryState>(OnGallery);
    }
    public override void Shutdown() { Close(); base.Shutdown(); }
    public void Open() => RaiseNetworkEvent(new ApartmentShopRequest());
    private void OnState(ApartmentShopState state)
    {
        _state = state;
        if (_window == null)
        {
            _window = new ApartmentTravelWindow(shop: true);
            _window.OnClose += Close; _window.Panel.OnClose += Close;
            _window.Panel.OnAction += Action; _window.OpenCentered();
        }
        _window.Title = "Магазин квартир";
        _preview = null; _gallery = null;
        Publish();
    }
    private void Publish()
    {
        if (_window == null || _state == null) return;
        var state = _state;
        _window.Panel.SetState("ApartmentShop", new TguiData().String("token", state.Token.ToString()).Object("preview", _preview ?? new TguiData()).Object("gallery", _gallery ?? new TguiData()).Number("number", state.Number).Bool("lobby", state.Lobby)
            .Number("balance", state.Balance)
            .String("pendingItemOrder", state.PendingItemOrder == Guid.Empty ? "" : state.PendingItemOrder.ToString())
            .Array("items", state.Furniture.Select(item => new TguiData().String("id", item.Id).String("name", item.Name)
                .String("category", item.Category).Number("price", item.Price)
                .Array("images", TguiSpriteImages.Shared.Item(item.Entity))))
            .Bool("legacy", state.Legacy).String("message", state.Message).Array("templates", state.Templates.Select(item =>
                new TguiData().String("id", item.Id).String("name", item.Name).Number("price", item.Price)
                    .String("description", item.Description).String("category", item.Category).Number("width", item.Width).Number("height", item.Height).Number("popularity", item.Popularity)
                    .Bool("owned", item.Owned).Bool("selected", item.Selected))).ToString(), "Магазин квартир");
    }
    private void OnGallery(ApartmentGalleryState ev)
    {
        if (_state == null || ev.Token != _state.Token) return;
        _galleryRequest++;
        _gallery = new TguiData().Bool("loaded", true).Number("page", ev.Page).Number("total", ev.Total)
            .Number("pageSize", ApartmentGalleryState.PageSize).String("error", ev.Error)
            .Array("paintings", ev.Paintings.Select(painting => new TguiData().String("id", painting.Id)
                .String("title", painting.Title).String("image", "data:image/png;base64," + painting.Preview).Bool("framed", painting.Framed)));
        Publish();
    }
    private void OnPreview(ApartmentShopPreview ev)
    {
        if (_state == null || ev.Token != _state.Token) return;
        var data = new TguiData().String("id", ev.Template).String("error", ev.Error);
        if (ev.Scene is { } scene)
        {
            var images = TguiSpriteImages.Shared;
            var walls = new TguiWallImages(images, scene.Objects, _prototypes);
            data.Number("x", scene.X).Number("y", scene.Y).Number("width", scene.Width).Number("height", scene.Height)
                .Array("tiles", scene.Tiles.Select(tile => new TguiData().String("kind", "tile").Number("x", tile.X).Number("y", tile.Y)
                    .Array("images", _prototypes.Index<ContentTileDefinition>(tile.Prototype).Sprite is { } path
                        ? new[] { new TguiData().String("url", images.TileFrame(path, tile.Variant)) } : Array.Empty<TguiData>())))
                .Array("objects", scene.Objects.Select(item =>
                {
                    var prototype = _prototypes.Index<EntityPrototype>(item.Prototype);
                    prototype.TryGetComponent<SpriteComponent>(out var sprite);
                    return new TguiData().String("kind", "entity").String("prototype", item.Prototype).Number("x", item.X).Number("y", item.Y)
                        .Number("rotation", item.Rotation).Bool("noRotation", sprite?.NoRotation ?? false)
                        .Number("depth", sprite?.DrawDepth ?? 0).Array("images", walls.Images(item));
                }));
        }
        _preview = data;
        Publish();
    }
    private void Action(string action, string payload)
    {
        if (_state == null) return;
        if (action == "close") { Close(); return; }
        if (action == "focus") { _window?.Panel.Web.FocusTextInput(); return; }
        if (!TguiActionData.TryParse(payload, out var fields, 1024)) return;
        Guid.TryParse(fields!.String("order"), out var order);
        var price = long.TryParse(fields.String("unitPrice"), out var parsedPrice) ? parsedPrice : -1;
        var template = fields.String("template") ?? "";
        if (action == "preview")
        {
            var token = _state.Token;
            _preview = null;
            Publish();
            Robust.Shared.Timing.Timer.Spawn(12000, () =>
            {
                if (_state?.Token != token || _preview != null) return;
                _preview = new TguiData().String("id", template).String("error", "Сервер не ответил. Повторите предпросмотр.");
                Publish();
            });
        }
        fields.TryInt("page", out var page);
        if (action == "gallery")
        {
            var token = _state.Token;
            var request = ++_galleryRequest;
            _gallery = null;
            Publish();
            Robust.Shared.Timing.Timer.Spawn(12000, () =>
            {
                if (_state?.Token != token || _galleryRequest != request || _gallery != null) return;
                _gallery = new TguiData().Bool("loaded", true).Number("page", page).String("error", "Сервер не ответил. Обновите галерею.");
                Publish();
            });
        }
        RaiseNetworkEvent(new ApartmentShopAction(_state.Token, action, template, order, price, page));
    }
    private void Close()
    {
        var window = _window; _window = null;
        if (_state != null) RaiseNetworkEvent(new ApartmentShopAction(_state.Token, "close"));
        _state = null; _preview = null; _gallery = null; _galleryRequest++;
        if (window == null) return;
        window.Close(); window.Panel.Web.ReleaseTextInput();
        Robust.Shared.Timing.Timer.Spawn(3000, () => {
#pragma warning disable CS0618
            if (!window.Disposed) window.Dispose();
#pragma warning restore CS0618
        });
    }
}
