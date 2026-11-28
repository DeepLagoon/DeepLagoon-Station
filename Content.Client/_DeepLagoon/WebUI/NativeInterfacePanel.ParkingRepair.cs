using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeInterfacePanel
{
    private string _parkingRepairShip = "";
    private readonly HashSet<string> _parkingRepairSelected = new();
    private readonly NativeParkingRepairPreview.View _parkingRepairMapView = new();
    private FancyWindow? _parkingRepairConfirmation;
    private string? _parkingRepairQuoteVersion;
    private string _parkingRepairPreviousToken = "";

    private void ParkingRepair(NativeUiData data, bool usable)
    {
        if (data.String("ship") != _parkingRepairShip || data.Bool("working"))
        {
            _parkingRepairShip = data.String("ship"); _parkingRepairSelected.Clear();
            _parkingRepairMapView.Framed = false;
            _parkingRepairSelected.UnionWith(data.String("selected").Split('|', StringSplitOptions.RemoveEmptyEntries));
            _parkingRepairQuoteVersion = null;
            CloseParkingRepairConfirmation();
        }
        var parts = data.Array("parts").ToArray();
        _parkingRepairSelected.IntersectWith(parts.Select(p => p.String("id")));
        if (_parkingRepairShip.Length == 0) { Text(_body, "Вставьте ID-карту с вашим шаттлом."); return; }
        if (!data.Bool("parked"))
        {
            Text(_body, "Для ремонта шаттл удерживается на бесплатной парковке до завершения работ.");
            Button(_body, "Поставить на парковку", () => Send("park"), usable);
        }
        if (data.Bool("working")) Text(_body, "Ремонт выполняется. Осталось: " + data.String("remaining") + " с.");
        var layout = new ParkingRepairLayout { HorizontalExpand = true };
        _body.AddChild(layout);
        var map = new NativeParkingRepairPreview(data, _parkingRepairSelected, _parkingRepairMapView);
        layout.AddChild(map);
        var menu = Column(); menu.MinWidth = 180;
        var scroll = new ScrollContainer { MaxHeight = 360, HorizontalExpand = true, HScrollEnabled = false };
        scroll.AddChild(menu); layout.AddChild(scroll);
        var enabled = usable && data.Bool("parked") && !data.Bool("working");
        foreach (var group in parts.GroupBy(p => p.String("kind")))
        {
            var label = group.Key switch { "tiles" => "Палуба", "structures" => "Стены и конструкции", _ => "Оборудование" };
            var select = new CheckBox { Text = label + " · " + group.Count(), Pressed = group.All(p => _parkingRepairSelected.Contains(p.String("id"))), Disabled = !enabled };
            select.OnToggled += args => { CloseParkingRepairConfirmation(); foreach (var part in group) { if (args.Pressed) _parkingRepairSelected.Add(part.String("id")); else _parkingRepairSelected.Remove(part.String("id")); } _body.RemoveAllChildren(); Parking(); };
            menu.AddChild(select);
            foreach (var part in group)
            {
                var id = part.String("id");
                var check = new CheckBox { Text = part.String("name") + $" ({part.String("x")}, {part.String("y")})", Pressed = _parkingRepairSelected.Contains(id), Disabled = !enabled };
                check.OnToggled += args => { CloseParkingRepairConfirmation(); if (args.Pressed) _parkingRepairSelected.Add(id); else _parkingRepairSelected.Remove(id); };
                menu.AddChild(check);
            }
        }
        Button(_body, "Починить всё", () =>
        {
            _parkingRepairSelected.Clear(); _parkingRepairSelected.UnionWith(parts.Select(p => p.String("id")));
            RequestQuote(new TguiData().String("ranges", parts.Length == 1 ? "0" : "0-" + (parts.Length - 1)).String("selectionVersion", data.String("selectionVersion")));
            _body.RemoveAllChildren(); Parking();
        }, enabled && parts.Length > 0);
        Button(_body, "Рассчитать выбранный ремонт", () => RequestQuote(new TguiData().String("selected", string.Join('|', _parkingRepairSelected))), enabled && _parkingRepairSelected.Count > 0);
        if (data.String("token").Length > 0)
        {
            Text(_body, "Материалы по рынку: " + data.String("materials") + " кр. · С комиссией верфи: " + data.String("credits") + " кр.");
            Text(_body, "Время: " + data.String("seconds") + " с. · Мгновенно: " + data.String("coins") + " LC.");
            Button(_body, "Подтвердить выбранный ремонт…", () => ShowParkingRepairConfirmation(data), enabled);
        }
        if (_parkingRepairQuoteVersion is { } version && version != data.String("selectionVersion"))
        {
            _parkingRepairQuoteVersion = null;
            if (enabled && data.String("token").Length > 0 && data.String("token") != _parkingRepairPreviousToken)
                ShowParkingRepairConfirmation(data);
        }
        void RequestQuote(TguiData selection)
        {
            CloseParkingRepairConfirmation();
            _parkingRepairPreviousToken = data.String("token"); _parkingRepairQuoteVersion = data.String("selectionVersion");
            Send("repairQuote", selection);
        }
    }

    private void CloseParkingRepairConfirmation()
    {
        var window = _parkingRepairConfirmation; _parkingRepairConfirmation = null;
        window?.Close(); window?.Dispose();
    }
    private void ShowParkingRepairConfirmation(NativeUiData repair)
    {
        var quoted = repair.String("selected").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (!quoted.SetEquals(_parkingRepairSelected)) return;
        CloseParkingRepairConfirmation();
        var window = _parkingRepairConfirmation = new FancyWindow { Title = "Подтверждение ремонта", MinSize = new Vector2(320, 220), SetSize = new Vector2(460, 260) };
        var body = Column(); body.SeparationOverride = 12; window.ContentsContainer.AddChild(body);
        Text(body, "Элементов к восстановлению: " + quoted.Count);
        Text(body, "Материалы: " + repair.String("materials") + " кр.");
        Text(body, "С комиссией верфи: " + repair.String("credits") + " кр.");
        Text(body, $"Время ремонта: {repair.Int("seconds") / 60} мин. {repair.Int("seconds") % 60} с.");
        Text(body, "Мгновенный ремонт: " + repair.String("coins") + " LC");
        var sent = false;
        Button(body, "Оплатить " + repair.String("credits") + " кр.", () => Confirm(false));
        Button(body, "Мгновенно за " + repair.String("coins") + " LC", () => Confirm(true));
        Button(body, "Отмена", CloseParkingRepairConfirmation);
        window.OnClose += () => { if (_parkingRepairConfirmation == window) _parkingRepairConfirmation = null; window.Dispose(); };
        window.OpenCentered();
        void Confirm(bool useCoins)
        {
            if (sent) return;
            var current = _data.Object("repair");
            if (current.String("ship") != repair.String("ship") || current.String("token") != repair.String("token") ||
                current.Bool("working") || !_data.Bool("id") || _data.Bool("busy") || !quoted.SetEquals(_parkingRepairSelected))
            { CloseParkingRepairConfirmation(); return; }
            sent = true;
            Send("repairConfirm", new TguiData().String("token", repair.String("token")).Bool("coins", useCoins));
            CloseParkingRepairConfirmation();
        }
    }
    protected override void ExitedTree()
    {
        CloseParkingRepairConfirmation();
        base.ExitedTree();
    }
}

internal sealed class ParkingRepairLayout : BoxContainer
{
    public ParkingRepairLayout() { Orientation = LayoutOrientation.Vertical; }
    protected override void Resized()
    {
        base.Resized();
        Orientation = Size.X >= 620 ? LayoutOrientation.Horizontal : LayoutOrientation.Vertical;
    }
}

/// <summary>Neutral status-map geometry with the repair device's missing-part overlay. No department regions.</summary>
internal sealed class NativeParkingRepairPreview : MapGridControl
{
    internal sealed class View { public Vector2 Offset; public float Range; public bool Framed; }
    private readonly NativeUiData _data;
    private readonly HashSet<string> _selected;
    private readonly Dictionary<string, List<(OwnedTexture Texture, Color Color)>> _textures = new();
    private readonly Vector2 _center;
    private readonly View _view;
    protected override bool Draggable { get; set; } = true;

    public NativeParkingRepairPreview(NativeUiData data, HashSet<string> selected, View view) : base(2, 4096, 48)
    {
        _data = data; _selected = selected; _view = view; SetSize = new Vector2(0, 320); MinHeight = 320; HorizontalExpand = true; RectClipContent = true; FitViewport = true;
        var all = data.Array("current").Concat(data.Array("parts")).ToArray();
        var min = all.Length == 0 ? Vector2.Zero : new Vector2(all.Min(p => p.Float("x")) - 1, all.Min(p => p.Float("y")) - 1);
        var max = all.Length == 0 ? Vector2.One : new Vector2(all.Max(p => p.Float("x")) + 1, all.Max(p => p.Float("y")) + 1);
        _center = (min + max) / 2;
        WorldRange = ActualRadarRange = view.Framed ? view.Range : Math.Max(2, Math.Max(max.X - min.X, max.Y - min.Y) * .6f);
        Offset = TargetOffset = view.Framed ? view.Offset : Vector2.Zero; view.Framed = true;
        foreach (var visual in data.Array("visuals"))
        {
            var textures = new List<(OwnedTexture, Color)>();
            foreach (var layer in visual.Array("images"))
            {
                using var image = NativeImage.Decode(layer.String("url"));
                if (image != null) textures.Add(((OwnedTexture)Texture.LoadFromImage(image, "parking-repair-preview"), Color.TryFromHex(layer.String("color"), out var tint) ? tint : Color.White));
            }
            _textures[visual.String("id")] = textures;
        }
    }
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromSrgb(new Color(16, 23, 27)));
        var all = _data.Array("current").Concat(_data.Array("parts")).ToArray();
        if (all.Length == 0) return;
        var scale = MinimapScale;
        var previous = handle.GetTransform();
        foreach (var part in _data.Array("current").OrderBy(p => p.Bool("tile") ? 0 : 1).Concat(_data.Array("parts").OrderBy(p => p.Bool("tile") ? 0 : 1)))
        {
            var ghost = part.String("id").Length > 0;
            var tint = ghost ? (_selected.Contains(part.String("id")) ? new Color(.3f, 1f, .6f, .85f) : new Color(1f, .5f, .1f, .45f)) : Color.White;
            var position = ScalePosition((new Vector2(part.Float("x"), part.Float("y")) - _center - Offset) * new Vector2(1, -1));
            handle.SetTransform(Matrix3x2.CreateRotation(-part.Float("rotation")) * Matrix3x2.CreateTranslation(position) * previous);
            var geometry = part.String("geometry");
            if (geometry.Length == 0) geometry = part.Bool("tile") ? "floor" : "equipment";
            var bounds = UIBox2.FromDimensions(new Vector2(-scale / 2), new Vector2(scale));
            if (geometry == "floor") handle.DrawRect(bounds, ghost ? tint.WithAlpha(.3f) : new Color(67, 82, 87));
            else if (geometry is "wall" or "door")
            {
                var inset = .165f * scale;
                var outline = new UIBox2(bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
                handle.DrawRect(outline, ghost ? tint : new Color(134, 147, 149), false);
                if (geometry == "door") handle.DrawLine(new Vector2(0, outline.Top), new Vector2(0, outline.Bottom), ghost ? tint : new Color(134, 147, 149));
            }
            else if (_textures.TryGetValue(part.String("visual"), out var textures))
                foreach (var (texture, color) in textures) handle.DrawTextureRect(texture, UIBox2.FromDimensions(new Vector2(-scale / 2), new Vector2(scale)), color * tint);
            if (ghost) handle.DrawRect(UIBox2.FromDimensions(new Vector2(-scale / 2), new Vector2(scale)), tint, false);
        }
        handle.SetTransform(previous);
    }
    protected override void ExitedTree()
    {
        _view.Offset = Offset; _view.Range = ActualRadarRange;
        foreach (var texture in _textures.Values.SelectMany(t => t)) texture.Texture.Dispose();
        _textures.Clear(); base.ExitedTree();
    }
}
