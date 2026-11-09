using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Containers.ItemSlots;
using Robust.Client.UserInterface;

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
        var data = new TguiData().Bool("id", state.IdPresent).Bool("hasShip", state.HasShip).Bool("parked", state.Parked).Bool("busy", state.Busy)
            .String("user", state.User).String("owner", state.OwnerName).String("shipName", state.ShipName).String("category", ParkingRules.CategoryName(state.Category))
            .Number("shipCategory", state.Category).Number("credits", state.Credits).String("coins", state.Coins < 0 ? "…" : state.Coins.ToString())
            .String("message", state.Message).String("description", ParkingRules.StorageDescription)
            .String("reviewToken", state.ReviewToken == Guid.Empty ? "" : state.ReviewToken.ToString()).String("reviewSlot", state.ReviewSlot.ToString())
            .String("buyToken", state.BuyToken.ToString())
            .Array("excluded", state.Excluded.Select(name => new TguiData().String("name", name)))
            .Array("docks", state.Docks.Select(dock => new TguiData().String("id", dock.Entity.ToString()).String("name", dock.Name)))
            .Array("places", state.Places.Select(place => new TguiData().String("id", place.Id.ToString()).Number("category", place.Category)
                .String("categoryName", ParkingRules.CategoryName(place.Category)).String("name", place.ShipName).String("owner", place.Owner)
                .String("shipCategory", ParkingRules.CategoryName(place.ShipCategory)).Number("price", place.Price).Bool("recovering", place.Recovering).Bool("paid", place.Paid)))
            .Array("categories", Enumerable.Range(0, 5).Select(category => new TguiData().Number("id", category).String("name", ParkingRules.CategoryName(category))
                .Number("count", state.Places.Count(place => place.Category == category)).Number("first", ParkingRules.SlotPrice(category, false))
                .Number("extra", ParkingRules.SlotPrice(category, true))));
        if (state.RecallMap is { } recall)
            data.Object("recallMap", BuildRecallData(recall));
        _window.Panel.SetState("ShuttleParking", data.ToString(), "Парковка шаттлов");
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
        if (!TguiActionData.TryParse(payload, out var data) || data == null) return;
        Guid.TryParse(data.String("slot"), out var slot);
        Guid.TryParse(data.String("token"), out var token);
        data.TryInt("category", out var category);
        NetEntity? dock = NetEntity.TryParse(data.String("dock") ?? "", out var parsed) ? parsed : null;
        if (action is "refresh" or "park" or "recall" or "preview" or "save" or "buy" or "redeem" or "cancel")
            SendMessage(new ParkingAction(action, slot, category, token, dock));
    }
}
