using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Apartments;

public sealed partial class ApartmentEditorSystem
{
    private ApartmentCommerceStateEvent? _commerce, _commerceResult;
    private bool _commerceAwaiting;
    private int _commerceSequence;

    private void RequestCommerce()
    {
        if (_state == null) return;
        var token = _state.Token;
        _commerceAwaiting = true;
        RaiseNetworkEvent(new ApartmentCommerceRequest(token));
        Timer.Spawn(12000, () =>
        {
            if (_state?.Token != token || !_commerceAwaiting) return;
            _commerceAwaiting = false;
            _commerceResult = new ApartmentCommerceStateEvent(token, -1, [], message: "Сервер не ответил. Повторите загрузку каталога.");
            Publish();
        });
    }

    private void OnCommerce(ApartmentCommerceStateEvent ev)
    {
        if (_state?.Token != ev.Token) return;
        _commerceAwaiting = false;
        _commerceResult = ev;
        _commerceSequence++;
        if (ev.Balance >= 0) _commerce = ev;
        Publish();
    }

    public int FurnitureStockFor(string id) => Catalog(id) is { } model ? ProductStock(model) : 0;

    private int ProductStock(ApartmentFurniturePrototype model)
    {
        if (model.ExistingOnly) return 1;
        if (_commerce?.Products.FirstOrDefault(item => item.Id == model.StockKey) is { } product) return product.Stock;
        if (!_prototypes.TryIndex<ApartmentFurniturePrototype>(model.StockKey, out var root)) return 0;
        return _state?.Layout.Concat(_state.Stored).Count(item => Catalog(item.Furniture)?.StockKey == root.ID) ?? 0;
    }

    private void AddCommerceData(TguiData data)
    {
        data.Number("applySequence", _applySequence).Number("commerceSequence", _commerceSequence).Number("balance", _commerce?.Balance ?? -1).String("balanceText", _commerce?.Balance.ToString(CultureInfo.InvariantCulture) ?? "")
            .Bool("commerceReady", _commerce != null && _commerceResult?.Balance >= 0)
            .String("commerceOrder", _commerceResult?.Order.ToString() ?? "")
            .Bool("commerceSuccess", _commerceResult?.Success ?? false).Bool("commerceCompleted", _commerceResult?.Completed ?? true)
            .String("commerceMessage", _commerceResult?.Message ?? "")
            .Array("products", (_commerce?.Products ?? []).Select(item => new TguiData()
                .String("id", item.Id).String("name", item.Name).String("category", item.Category).Number("price", item.Price)
                .Number("packSize", item.PackSize).Number("stock", item.Stock)
                .Array("variants", item.Variants.Select(id => new TguiData().String("id", id)))));
    }

    private void PurchaseFurniture(string payload)
    {
        if (_state == null || !TguiActionData.TryParse(payload, out var fields, 1024) ||
            !Guid.TryParse(fields!.String("order"), out var order) || order == Guid.Empty ||
            !int.TryParse(fields.String("quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity < 1 ||
            !long.TryParse(fields.String("unitPrice"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) || price is < 1 or > 500 ||
            fields.String("product") is not { Length: > 0 and <= 128 } product) return;
        RaiseNetworkEvent(new ApartmentFurniturePurchaseEvent(_state.Token, order, product, quantity, price));
    }
}
