using System.Globalization;
using System.Linq;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class NativeApartmentEditor
{
    private readonly Label _wallet = new() { FontColorOverride = Color.FromHex("#FFC76B") };
    private readonly BoxContainer _commerceRows = NativeInterfacePanel.Column();
    private LineEdit _commerceSearch = default!;
    private TguiData? _nativePurchase;
    private string _nativeOrder = "";

    private void InitializeCommerce()
    {
        var wallet = NativeInterfacePanel.Row(); AddChild(wallet);
        NativeInterfacePanel.Text(wallet, "Баланс:"); wallet.AddChild(_wallet);
        var search = NativeInterfacePanel.Row(); AddChild(search);
        _commerceSearch = NativeInterfacePanel.Field(search, "Поиск по каталогу");
        _commerceSearch.OnTextChanged += _ => RenderCommerce();
        var scroll = new ScrollContainer { MaxHeight = 260, HorizontalExpand = true, HScrollEnabled = false };
        scroll.AddChild(_commerceRows); AddChild(scroll);
    }

    private void UpdateCommerce()
    {
        _wallet.Text = long.TryParse(_state.String("balanceText"), out var balance) ? balance.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " LC" : "— LC";
        if (_nativePurchase != null && _state.String("commerceOrder") == _nativeOrder && _state.Bool("commerceCompleted"))
        { _nativePurchase = null; _nativeOrder = ""; }
        RenderCommerce();
    }

    private void RenderCommerce()
    {
        _commerceRows.RemoveAllChildren();
        if (_state.String("commerceMessage").Length > 0) NativeInterfacePanel.Text(_commerceRows, _state.String("commerceMessage"));
        if (_nativePurchase != null)
        {
            NativeInterfacePanel.Text(_commerceRows, "Проверка покупки. Повтор безопасен: используется тот же идентификатор запроса.");
            NativeInterfacePanel.Button(_commerceRows, "Повторить запрос покупки", () => Send("buyFurniture", _nativePurchase));
            return;
        }
        if (!_state.Bool("commerceReady"))
        {
            NativeInterfacePanel.Text(_commerceRows, "Загрузка баланса и запасов…");
            NativeInterfacePanel.Button(_commerceRows, "Повторить загрузку", () => Send("refreshCommerce"));
            return;
        }
        foreach (var product in _state.Array("products").Where(item => item.String("name").Contains(_commerceSearch.Text, StringComparison.CurrentCultureIgnoreCase)))
        {
            if (!long.TryParse(product.String("price"), out var price) || price < 0) continue;
            var box = NativeInterfacePanel.Column(); _commerceRows.AddChild(box);
            NativeInterfacePanel.Text(box, product.String("name") + " · запас " + product.String("stock") + " · " + product.String("packSize") + " шт. за " + price + " LC");
            if (product.Array("variants").Count() > 1) NativeInterfacePanel.Text(box, "Все секции вместе, запас общий. Выберите секцию в списке размещения.");
            var quantity = NativeInterfacePanel.Field(box, "Количество наборов", "1");
            var confirmQuantity = 0;
            var buy = NativeInterfacePanel.Button(box, "Купить наборы", () => { });
            buy.OnPressed += _ =>
            {
                if (!int.TryParse(quantity.Text, out var count) || count < 1) { _status.Text = "Введите целое число наборов от 1."; return; }
                if (price > long.MaxValue / count) { _status.Text = "Слишком большое количество наборов."; return; }
                var total = price * count;
                if (!long.TryParse(_state.String("balanceText"), out var balance) || total > balance) { _status.Text = "Недостаточно Lagoon Coin."; return; }
                if (confirmQuantity != count) { confirmQuantity = count; buy.Text = "Подтвердить: " + count + " наборов за " + total + " LC"; return; }
                _nativeOrder = Guid.NewGuid().ToString();
                _nativePurchase = new TguiData().String("order", _nativeOrder).String("product", product.String("id"))
                    .Number("quantity", count).Number("unitPrice", price);
                Send("buyFurniture", _nativePurchase);
                RenderCommerce();
            };
        }
    }

    private void PlacementOptions()
    {
        _catalog.RemoveAllChildren();
        var options = _state.Array("catalog").Where(item => !item.Bool("existingOnly") && item.Int("maxCount") > 0)
            .Select(item => (item.String("id"), item.String("name"))).ToArray();
        if (!options.Any(item => item.Item1 == _furniture)) _furniture = options.FirstOrDefault().Item1 ?? "";
        NativeInterfacePanel.Options(_catalog, options, id => _furniture = id, _furniture);
    }

    private bool CanAddFurniture()
    {
        if (_nativePurchase != null || _draft.Count >= Content.Shared._DeepLagoon.Apartments.ApartmentLayout.SafetyLimit) return false;
        var definitions = _state.Array("catalog").ToDictionary(item => item.String("id"));
        if (!definitions.TryGetValue(_furniture, out var model)) return false;
        var product = model.String("product", _furniture);
        return _draft.Count(item =>
        {
            var data = NativeUiData.Parse(item.ToString());
            return definitions.TryGetValue(data.String("furniture"), out var definition) && !definition.Bool("existingOnly") && definition.String("product", data.String("furniture")) == product;
        }) < model.Int("maxCount");
    }
}
