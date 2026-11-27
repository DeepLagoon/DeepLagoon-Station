using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Exchange;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.Exchange;

/// <summary>Native cartridge view, also hosted by the PDA's Orbital program surface.</summary>
public sealed partial class ExchangeView : BoxContainer
{
    public event Action<ExchangeAction, string, long, Guid, int, string, string, string>? Request;
    private readonly Label _bank, _status, _price, _change, _wallet, _liquidity, _quote, _materialValue;
    private readonly Button _buy, _sell, _deposit;
    private readonly OptionButton _asset, _material;
    private readonly LineEdit _quantity;
    private readonly ChartPanel _cryptoChart, _materialChart;
    private readonly BoxContainer _balances;
    private ExchangeSnapshot? _snapshot;
    private string _selected = "contrib";
    private string _selectedMaterial = "Steel";
    private int _period = 24;
    private bool _applying;
    private static readonly string[] Assets = ["contrib", "ideas", "goida"];
    private static readonly Color[] Colours = [Color.FromHex("#45D9D0"), Color.FromHex("#B69AFF"), Color.FromHex("#F6C567")];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public ExchangeView()
    {
        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 8;
        Margin = new Thickness(10);
        MinWidth = 450;
        HorizontalExpand = true;
        var title = Row();
        title.AddChild(new Label { Text = L("title"), HorizontalExpand = true, FontColorOverride = Colours[0] });
        _bank = new Label { Text = L("connecting") };
        title.AddChild(_bank);
        AddChild(title);
        var periods = Row();
        foreach (var hours in new[] { 1, 6, 24, 168 })
        {
            var button = new Button { Text = hours == 168 ? L("week") : hours + L("hours"), HorizontalExpand = true, ToggleMode = true, Pressed = hours == 24 };
            periods.AddChild(button);
            button.OnPressed += _ =>
            {
                _period = hours;
                foreach (var child in periods.Children) if (child is Button b) b.Pressed = b == button;
                Refresh();
            };
        }
        var refresh = new Button { Text = "↻", ToolTip = L("refresh") };
        refresh.OnPressed += _ => Refresh();
        periods.AddChild(refresh);
        AddChild(periods);
        var tabs = new TabContainer { HorizontalExpand = true, VerticalExpand = true };
        AddChild(tabs);
        var crypto = Column();
        tabs.AddChild(crypto);
        tabs.SetTabTitle(0, L("crypto"));
        var materials = Column();
        tabs.AddChild(materials);
        tabs.SetTabTitle(1, L("materials"));
        InitializeWallet(tabs);
        _balances = Row();
        crypto.AddChild(_balances);
        var hero = Row();
        _asset = new OptionButton { MinWidth = 110 };
        foreach (var symbol in new[] { "CTB", "IDEA", "GOI" }) _asset.AddItem(symbol);
        _asset.OnItemSelected += e => { _asset.SelectId(e.Id); _selected = Assets[e.Id]; Render(); Refresh(); };
        hero.AddChild(_asset);
        _price = new Label { Text = "—", HorizontalExpand = true, FontColorOverride = Colours[0] };
        _change = new Label { Text = "—" };
        hero.AddChild(_price); hero.AddChild(_change);
        crypto.AddChild(hero);
        _cryptoChart = new ChartPanel(Colours[0]);
        crypto.AddChild(_cryptoChart);
        _wallet = new Label { Text = L("wallet") + ": —" };
        _liquidity = new Label { Text = L("liquidity") + ": —" };
        crypto.AddChild(_wallet); crypto.AddChild(_liquidity);
        var order = Row();
        order.AddChild(new Label { Text = L("quantity") });
        _quantity = new LineEdit { Text = "1", MinWidth = 100, HorizontalExpand = true };
        _quantity.OnTextEntered += _ => Refresh();
        order.AddChild(_quantity);
        var calculate = new Button { Text = L("quote") };
        calculate.OnPressed += _ => Refresh();
        order.AddChild(calculate);
        crypto.AddChild(order);
        _quote = new Label { Text = L("fee"), ClipText = true, ToolTip = L("fee") };
        crypto.AddChild(_quote);
        var actions = Row();
        _buy = new Button { Text = L("buy"), HorizontalExpand = true, Disabled = true, Modulate = Color.FromHex("#9AE9D4") };
        _sell = new Button { Text = L("sell"), HorizontalExpand = true, Disabled = true, Modulate = Color.FromHex("#F0C49A") };
        _buy.OnPressed += _ => Trade(ExchangeAction.Buy);
        _sell.OnPressed += _ => Trade(ExchangeAction.Sell);
        _quantity.OnTextChanged += _ => { if (!_applying) { _buy.Disabled = true; _sell.Disabled = true; } };
        actions.AddChild(_buy); actions.AddChild(_sell);
        crypto.AddChild(actions);
        _deposit = new Button { Text = L("deposit"), ToolTip = L("deposit-help") };
        _deposit.OnPressed += _ => Send(ExchangeAction.Deposit);
        crypto.AddChild(_deposit);
        _material = new OptionButton { HorizontalExpand = true };
        _material.OnItemSelected += e =>
        {
            _material.SelectId(e.Id);
            if (_snapshot?.Materials.ElementAtOrDefault(e.Id) is { } selected) _selectedMaterial = selected.Id;
            RenderMaterial();
        };
        materials.AddChild(_material);
        _materialValue = new Label { Text = "—" };
        materials.AddChild(_materialValue);
        _materialChart = new ChartPanel(Color.FromHex("#8DDE9C"));
        materials.AddChild(_materialChart);
        materials.AddChild(new Label { Text = L("index-help"), ClipText = true, ToolTip = L("index-help") });
        _status = new Label { Text = L("connecting"), FontColorOverride = Color.FromHex("#A9B8CA"), ClipText = true };
        AddChild(_status);
    }

    public void Refresh() => Send(ExchangeAction.Refresh);

    private long Units()
    {
        return decimal.TryParse(_quantity.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, Invariant, out var quantity) &&
            quantity is > 0 and <= 1000 && quantity * 1000 == decimal.Truncate(quantity * 1000)
                ? (long)(quantity * 1000) : 0;
    }
    private void Send(ExchangeAction action)
    {
        var units = Units();
        if (units == 0) { _status.Text = L("error-quantity"); return; }
        Request?.Invoke(action, _selected, units, _snapshot?.Quote ?? Guid.Empty, _period, "", "", "");
    }
    private void Trade(ExchangeAction action)
    {
        _buy.Disabled = true;
        _sell.Disabled = true;
        _status.Text = L("processing");
        Send(action);
    }

    public bool Apply(ExchangeSnapshot snapshot)
    {
        if (snapshot.Assets.Length == 0)
        {
            _status.Text = L("error-" + snapshot.Status);
            _buy.Disabled = true; _sell.Disabled = true;
            _deposit.Disabled = true;
            _confirmTransfer.Disabled = true;
            _changePassword.Disabled = _snapshot?.WalletUnlocked != true;
            _loginSubmitted = false;
            return true;
        }
        if (snapshot.PeriodHours != _period || snapshot.SelectedAsset != _selected || snapshot.Units != Units()) return false;
        _snapshot = snapshot;
        _bank.Text = snapshot.HasBank ? L("spessos") + ": " + Money(snapshot.Bank) : L("cash-payment");
        _status.Text = snapshot.Status is "deposited" or "transferred" or "cash_paid" or "password_changed" ? L(snapshot.Status) :
            snapshot.Status.Length > 0 ? L("error-" + snapshot.Status) : L("updated") + " " + Time(snapshot.At);
        _balances.RemoveAllChildren();
        foreach (var asset in snapshot.Assets)
        {
            var panel = new PanelContainer { HorizontalExpand = true, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#142031") } };
            panel.AddChild(new Label { Text = asset.Symbol + "  " + (snapshot.WalletUnlocked ? Coins(asset.WalletUnits) : "—"), Margin = new Thickness(8, 6), FontColorOverride = Colours[Array.IndexOf(Assets, asset.Id)] });
            _balances.AddChild(panel);
        }
        _applying = true;
        _material.Clear();
        var chosen = 0;
        for (var i = 0; i < snapshot.Materials.Length; i++)
        {
            _material.AddItem(L("material-" + snapshot.Materials[i].Id), i);
            if (snapshot.Materials[i].Id == _selectedMaterial) chosen = i;
        }
        if (snapshot.Materials.Length > 0)
        { _material.SelectId(chosen); _selectedMaterial = snapshot.Materials[chosen].Id; }
        _applying = false;
        RenderWallet();
        Render();
        return true;
    }

    private void Render()
    {
        var asset = _snapshot?.Assets.FirstOrDefault(a => a.Id == _selected);
        if (asset == null) return;
        var colour = Colours[Array.IndexOf(Assets, _selected)];
        _price.Text = Money(asset.Price) + " / " + asset.Symbol;
        _price.FontColorOverride = colour;
        var change = asset.History.Length > 1 && asset.History[0].Price > 0 ? (asset.Price / asset.History[0].Price - 1) * 100 : (double?)null;
        _change.Text = change is { } percentage ? percentage.ToString("+0.00;-0.00;0.00", Invariant) + "%" : "—";
        _change.FontColorOverride = change is < 0 ? Color.FromHex("#F18E99") : Color.FromHex("#86DDAD");
        _cryptoChart.Set(asset.History, colour, false);
        _wallet.Text = L("wallet") + " " + _snapshot!.Address + ": " +
            (_snapshot.WalletUnlocked ? Coins(asset.WalletUnits) + " " + asset.Symbol : L("wallet-locked"));
        _liquidity.Text = L("liquidity") + ": " + Money(asset.Liquidity);
        var quoted = _snapshot!.SelectedAsset == _selected && _snapshot.Units == Units();
        _buy.Disabled = !quoted || !_snapshot.Enabled || !_snapshot.WalletUnlocked || !_snapshot.HasBank || _snapshot.BuyCost <= 0 || _snapshot.BuyCost > _snapshot.Bank;
        _sell.Disabled = !quoted || !_snapshot.Enabled || !_snapshot.WalletUnlocked || !_snapshot.CanSell || asset.WalletUnits < Units();
        _deposit.Disabled = !_snapshot.Enabled || !_snapshot.WalletUnlocked;
        _buy.Text = quoted ? L("buy") + " · " + Money(_snapshot.BuyCost) : L("buy");
        _sell.Text = quoted ? L("sell") + " · " + Money(_snapshot.SellProceeds) : L("sell");
        _quote.Text = L("fee");
        RenderMaterial();
    }

    private void RenderMaterial()
    {
        var material = _snapshot?.Materials.FirstOrDefault(m => m.Id == _selectedMaterial);
        if (material == null) return;
        var index = material.BasePrice > 0 ? material.IndexPrice / material.BasePrice * 100 : 0;
        _materialValue.Text = L("index") + ": " + index.ToString("0.00", Invariant) +
            (material.Id.EndsWith("Index", StringComparison.Ordinal) ? "" : " · " + L("anchor") + ": " + Money(material.BasePrice) +
                "\n" + L("reference-rate") + ": " + Money(material.IndexPrice));
        _materialChart.Set(material.History.Select(p => new ExchangePoint(p.At,
            material.BasePrice > 0 ? p.Price / material.BasePrice * 100 : 0, p.Volume)).ToArray(), Color.FromHex("#8DDE9C"), true);
    }

    private static BoxContainer Row() => new() { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 8, HorizontalExpand = true };
    private static BoxContainer Column() => new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8, Margin = new Thickness(4, 10), HorizontalExpand = true };
    internal static string L(string key) => Loc.GetString("dl-exchange-" + key);
    internal static string Money(double value) => "$" + value.ToString("N2", Invariant);
    internal static string Coins(long units) => (units / 1000d).ToString("0.###", Invariant);
    internal static string Time(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM HH:mm", Invariant);

    private sealed class ChartPanel : BoxContainer
    {
        private readonly Label _range, _dates;
        private readonly ExchangeChart _chart;
        public ChartPanel(Color colour)
        {
            Orientation = LayoutOrientation.Vertical;
            HorizontalExpand = true;
            SeparationOverride = 4;
            _range = new Label { Text = L("history-empty"), FontColorOverride = Color.FromHex("#A9B8CA") };
            AddChild(_range);
            _chart = new ExchangeChart { MinHeight = 210, HorizontalExpand = true, MouseFilter = MouseFilterMode.Stop };
            AddChild(_chart);
            _dates = new Label { Text = "—", FontColorOverride = Color.FromHex("#8090A8") };
            AddChild(_dates);
        }
        public void Set(ExchangePoint[] points, Color colour, bool index)
        {
            _chart.Set(points, colour, index);
            _range.Text = points.Length > 1 ?
                "Y: " + _chart.Minimum.ToString("0.00", Invariant) + " — " + _chart.Maximum.ToString("0.00", Invariant) +
                " · " + (index ? L("index") : L("price")) + " · " + L("volume") : L("history-empty");
            _dates.Text = points.Length > 0 ? Time(points[0].At) + "  —  " + Time(points[^1].At) : "—";
        }
    }
}

public sealed class ExchangeChart : Control
{
    private ExchangePoint[] _points = [];
    private Color _colour = Color.Cyan;
    private float? _hover;
    private bool _index;
    public double Minimum { get; private set; }
    public double Maximum { get; private set; } = 1;
    public void Set(ExchangePoint[] points, Color colour, bool index)
    {
        _points = points.Where(p => double.IsFinite(p.Price) && p.Price >= 0).OrderBy(p => p.At).ToArray();
        _colour = colour; _index = index;
        if (_points.Length == 0) { Minimum = 0; Maximum = 1; return; }
        var minimum = _points.Min(p => p.Price); var maximum = _points.Max(p => p.Price);
        var padding = Math.Max((maximum - minimum) * .12, Math.Max(1, maximum) * .005);
        Minimum = Math.Max(0, minimum - padding); Maximum = maximum + padding;
    }
    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        _hover = Math.Clamp(args.RelativePosition.X / Math.Max(1, Width), 0, 1);
        if (_points.Length == 0) return;
        var target = _points[0].At + (_points[^1].At - _points[0].At) * _hover.Value;
        var point = _points.OrderBy(p => Math.Abs(p.At - target)).First();
        ToolTip = ExchangeView.Time(point.At) + "\n" + (_index ? point.Price.ToString("0.00", CultureInfo.InvariantCulture) : ExchangeView.Money(point.Price)) +
            "\n" + ExchangeView.L("volume") + ": " + (_index ? point.Volume.ToString() : ExchangeView.Coins(point.Volume));
    }
    protected override void MouseExited() { base.MouseExited(); _hover = null; }
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromHex("#0C1422"));
        var height = Math.Max(1, PixelHeight - 36);
        for (var i = 0; i <= 4; i++)
        {
            var y = height * i / 4f;
            handle.DrawLine(new Vector2(0, y), new Vector2(PixelWidth, y), Color.FromHex("#223047"));
            var x = PixelWidth * i / 4f;
            handle.DrawLine(new Vector2(x, 0), new Vector2(x, height), Color.FromHex("#18263A"));
        }
        if (_points.Length == 0) return;
        var start = _points[0].At; var span = Math.Max(1, _points[^1].At - start);
        var volume = Math.Max(1, _points.Max(p => p.Volume));
        Vector2? previous = null;
        foreach (var p in _points)
        {
            var point = new Vector2((float)((p.At - start) / (double)span * PixelWidth),
                (float)(height - (p.Price - Minimum) / (Maximum - Minimum) * (height - 6)) - 3);
            if (previous is { } from)
            {
                handle.DrawRect(new UIBox2(from.X, point.Y, point.X + 1, height), _colour.WithAlpha(.08f));
                handle.DrawLine(from, point, _colour);
            }
            previous = point;
            var bar = (float)(p.Volume / (double)volume * 25);
            handle.DrawRect(new UIBox2(point.X, PixelHeight - bar, point.X + Math.Max(2, PixelWidth / Math.Max(1, _points.Length) * .5f), PixelHeight), _colour.WithAlpha(.35f));
        }
        if (previous is { } last) handle.DrawCircle(last, 2.5f, _colour);
        if (_hover is { } hover)
            handle.DrawLine(new Vector2(hover * PixelWidth, 0), new Vector2(hover * PixelWidth, PixelHeight), Color.FromHex("#8496AF"));
    }
}
