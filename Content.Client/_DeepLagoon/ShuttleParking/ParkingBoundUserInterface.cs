using System.Linq;
using System.Numerics;
using System.Threading;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Maps;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;
using Robust.Shared.Map;
using Robust.Client.UserInterface;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Client._DeepLagoon.ShuttleParking;

public sealed class ParkingWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(inheritChatAppearance: false);
    public ParkingWindow()
    {
        Title = "Парковка шаттлов";
        MinSize = new Vector2(320, 220);
        var available = IoCManager.Resolve<IUserInterfaceManager>().WindowRoot.Size;
        SetSize = available.X > 0 && available.Y > 0
            ? Vector2.Min(new Vector2(720, 720), Vector2.Max(MinSize, available - new Vector2(24)))
            : new Vector2(720, 720);
        ContentsContainer.AddChild(Panel);
    }
}

public sealed class ParkingBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private ParkingWindow? _window;
    private CancellationTokenSource? _returnRefresh;
    private string[] _repairPartIds = [];
    private string _repairSelectionVersion = "";
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<ParkingWindow>();
        _window.Panel.OnClose += Close;
        _window.Panel.OnAction += Action;
        _window.Panel.SetState("ShuttleParking", new TguiData().String("message", "Загрузка…").ToString(), "Парковка шаттлов");
        SendMessage(new ParkingAction("refresh"));
    }
    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is not ParkingUpdate state || _window == null) return;
        var receivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var data = new TguiData().Bool("id", state.IdPresent).Bool("hasShip", state.HasShip).Bool("parked", state.Parked).Bool("busy", state.Busy)
            .Bool("repairEnabled", state.RepairEnabled)
            .Bool("recoveredParking", state.RecoveredParking).String("parkingSnapshot", state.ParkingSnapshot)
            .String("user", state.User).String("owner", state.OwnerName).String("shipName", state.ShipName).String("category", state.RecoveredParking && state.Category < 0 ? "—" : ParkingRules.CategoryName(state.Category))
            .Number("shipCategory", state.Category).String("credits", state.Credits.ToString()).String("coins", state.Coins < 0 ? "…" : state.Coins.ToString())
            .String("message", state.Message).String("description", ParkingRules.StorageDescription)
            .String("reviewToken", state.ReviewToken == Guid.Empty ? "" : state.ReviewToken.ToString()).String("reviewSlot", state.ReviewSlot.ToString())
            .String("buyToken", state.BuyToken.ToString())
            .Array("excluded", state.Excluded.Select(name => new TguiData().String("name", name)))
            .Array("docks", state.Docks.Select(dock => new TguiData().String("id", dock.Entity.ToString()).String("name", dock.Name)))
            .Array("places", state.Places.Select(place => new TguiData().String("id", place.Id.ToString()).Number("category", place.Category)
                .String("categoryName", ParkingRules.CategoryName(place.Category)).String("name", place.ShipName).String("owner", place.Owner)
                .String("shipCategory", ParkingRules.CategoryName(place.ShipCategory)).Number("price", place.Price).Bool("recovering", place.Recovering)
                .Bool("paid", place.Paid).Number("returnWaitSeconds", place.ReturnWaitSeconds)
                .Number("returnReadyAtMs", place.ReturnWaitSeconds > 0 ? receivedAtMs + place.ReturnWaitSeconds * 1000L : 0)))
            .Array("categories", Enumerable.Range(0, 5).Select(category => new TguiData().Number("id", category).String("name", ParkingRules.CategoryName(category))
                .Number("count", state.Places.Count(place => place.Category == category)).Number("first", ParkingRules.SlotPrice(category, false))
                .Number("extra", ParkingRules.SlotPrice(category, true))));
        if (state.RecallMap is { } recall)
            data.Object("recallMap", BuildRecallData(recall));
        if (state.Repair is { } repair)
        {
            _repairPartIds = repair.Parts.Select(p => p.Id).ToArray();
            _repairSelectionVersion = Guid.NewGuid().ToString();
            data.Object("repair", BuildRepairData(repair));
        }
        _window.Panel.SetState("ShuttleParking", data.ToString(), "Парковка шаттлов");
        CancelReturnRefresh();
        var nextReturn = state.Places.Where(place => place.ReturnWaitSeconds > 0).Select(place => place.ReturnWaitSeconds).DefaultIfEmpty().Min();
        if (state.Repair is { Working: true } working)
            nextReturn = nextReturn > 0 ? Math.Min(nextReturn, Math.Max(1, working.Remaining)) : Math.Max(1, working.Remaining);
        if (nextReturn > 0)
        {
            _returnRefresh = new CancellationTokenSource();
            Timer.Spawn(TimeSpan.FromMilliseconds(nextReturn * 1000 + 250), () =>
            {
                if (IsOpened)
                    SendMessage(new ParkingAction("refresh"));
            }, _returnRefresh.Token);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CancelReturnRefresh();
        base.Dispose(disposing);
    }

    private void CancelReturnRefresh()
    {
        _returnRefresh?.Cancel();
        _returnRefresh?.Dispose();
        _returnRefresh = null;
    }

    private static TguiData BuildRecallData(ParkingRecallMap map)
    {
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        void Include(Vector2 point)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        foreach (var polygon in map.StationHull)
            foreach (var vertex in polygon)
                Include(vertex);

        var shipVertices = map.ShuttleHull.SelectMany(polygon => polygon).ToArray();
        foreach (var port in map.Ports)
        {
            Include(port.Position);
            if (port.ShipOrigin is not { } origin)
                continue;

            foreach (var vertex in shipVertices)
                Include(origin + port.ShipAngle.RotateVec(vertex));
        }

        if (!float.IsFinite(min.X))
            min = max = Vector2.Zero;

        var padding = new Vector2(4);
        var size = max - min + 2 * padding;
        static TguiData Point(Vector2 vertex) => new TguiData().Number("x", vertex.X).Number("y", vertex.Y);
        static TguiData Polygon(Vector2[] vertices) => new TguiData().Array("points", vertices.Select(Point));
        return new TguiData().Number("x", min.X - padding.X).Number("y", min.Y - padding.Y)
            .Number("width", size.X).Number("height", size.Y)
            .Array("station", map.StationHull.Select(Polygon))
            .Array("ship", map.ShuttleHull.Select(Polygon))
            .Array("ports", map.Ports.Select(port => Point(port.Position)
                .String("id", port.Entity.ToString()).String("name", port.Name)
                .String("status", port.Status.ToString().ToLowerInvariant())
                .Bool("hasPose", port.ShipOrigin.HasValue)
                .Number("shipX", port.ShipOrigin?.X ?? 0).Number("shipY", port.ShipOrigin?.Y ?? 0)
                .Number("shipAngle", (float)port.ShipAngle.Theta)));
    }
    private void Action(string action, string payload)
    {
        if (action == "close") { Close(); return; }
        if (action == "id") { SendMessage(new ItemSlotButtonPressedEvent("parking-id")); return; }
        if (!TguiActionData.TryParse(payload, out var data, 1048576) || data == null) return;
        if (action is "repairQuote" or "repairConfirm")
        {
            Guid.TryParse(data.String("token"), out var repairToken);
            var selected = (data.String("selected") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (data.String("ranges") is { } ranges)
            {
                if (data.String("selectionVersion") != _repairSelectionVersion) return;
                var indices = new HashSet<int>();
                foreach (var range in ranges.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var bounds = range.Split('-');
                    if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], out var first)) return;
                    var last = first;
                    if (bounds.Length == 2 && !int.TryParse(bounds[1], out last)) return;
                    if (first < 0 || last < first || last >= _repairPartIds.Length) return;
                    for (var i = first; i <= last; i++) indices.Add(i);
                }
                selected = indices.Select(i => _repairPartIds[i]).ToArray();
            }
            else if (data.String("bits") is { } bits)
            {
                if (data.String("selectionVersion") != _repairSelectionVersion) return;
                byte[] bitmap;
                try { bitmap = Convert.FromBase64String(bits); } catch (Exception) { return; }
                if (bitmap.Length != (_repairPartIds.Length + 7) / 8) return;
                selected = _repairPartIds.Where((_, index) => (bitmap[index / 8] & (1 << (index % 8))) != 0).ToArray();
            }
            SendMessage(new ParkingRepairAction(action == "repairQuote" ? "quote" : "confirm",
                selected, repairToken, data.String("coins") == "true"));
            return;
        }
        Guid.TryParse(data.String("slot"), out var slot);
        Guid.TryParse(data.String("token"), out var token);
        data.TryInt("category", out var category);
        NetEntity? dock = NetEntity.TryParse(data.String("dock") ?? "", out var parsed) ? parsed : null;
        if (action is "refresh" or "park" or "recall" or "preview" or "save" or "buy" or "redeem" or "cancel")
            SendMessage(new ParkingAction(action, slot, category, token, dock));
    }

    private TguiData BuildRepairData(ParkingRepairView repair)
    {
        var images = TguiSpriteImages.Shared;
        var tiles = IoCManager.Resolve<ITileDefinitionManager>();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        var tags = EntMan.System<TagSystem>();
        var assets = new Dictionary<string, TguiData>();
        TguiData Visual(string id, string proto, Vector2 position, Angle angle, int tile, byte variant = 0)
        {
            var key = proto.Length > 0 ? proto : $"tile:{tile}:{variant}";
            var result = new TguiData().String("id", id).String("prototype", proto).String("visual", key).Number("x", position.X).Number("y", position.Y)
                .Number("rotation", (float)angle.Theta).Bool("tile", proto.Length == 0);
            // Match the status/crew navigation map's wall and door classification. Department
            // regions and beacons are intentionally absent from the remote repair snapshot.
            var geometry = proto.Length == 0 ? "floor" : "equipment";
            if (proto.Length > 0 && prototypes.TryIndex<EntityPrototype>(proto, out var entityDefinition))
            {
                if (entityDefinition.TryGetComponent<Content.Shared.Pinpointer.NavMapDoorComponent>(out _)) geometry = "door";
                else if (entityDefinition.TryGetComponent<TagComponent>(out var tag) && (tags.HasTag(tag, "Wall") || tags.HasTag(tag, "Window"))) geometry = "wall";
            }
            result.String("geometry", geometry);
            if (!assets.ContainsKey(key))
            {
                var asset = new TguiData().String("id", key);
                if (proto.Length > 0) asset.Array("images", images.MapItem(proto));
                else if (tiles.TryGetDefinition((ushort)tile, out var definition) && definition is ContentTileDefinition { Sprite: { } sprite })
                    asset.Array("images", new[] { new TguiData().String("url", images.TileFrame(sprite, variant)).String("color", "#ffffff") });
                assets[key] = asset;
            }
            return result;
        }
        return new TguiData().String("ship", repair.Ship).Bool("parked", repair.Parked).Bool("working", repair.Working)
            .String("selectionVersion", _repairSelectionVersion)
            .Number("remaining", repair.Remaining).String("token", repair.Token == Guid.Empty ? "" : repair.Token.ToString())
            .String("credits", repair.Credits.ToString()).String("coins", repair.Coins.ToString()).String("materials", repair.Materials.ToString())
            .Number("seconds", repair.Seconds).String("selected", string.Join('|', repair.Selected))
            .Array("parts", repair.Parts.Select(part => Visual(part.Id, part.Prototype, part.Position, part.Rotation, part.Tile)
                .String("name", part.Name).String("kind", part.Kind)))
            .Array("current", repair.Current.Select(part => Visual("", part.Prototype, part.Position, part.Rotation, part.Tile, part.Variant)))
            .Array("visuals", assets.Values);
    }
}
