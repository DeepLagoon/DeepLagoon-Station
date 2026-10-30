using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public bool CanDecorate(EntityUid user, EntityUid target) =>
        _players.TryGetSessionByEntity(user, out var session) && CanManage(session) &&
        _visits.TryGetValue(session.UserId, out var visit) && visit.Apartment.Owner == session.UserId &&
        Transform(target).GridUid == visit.Apartment.Grid;

    public bool SaveDecoration(EntityUid target)
    {
        var grid = Transform(target).GridUid;
        var instance = _instances.Values.FirstOrDefault(home => home.Grid == grid);
        return instance != null && SaveApartment(instance);
    }

    private List<ApartmentShopEntry> ShopItems() => _prototypes.EnumeratePrototypes<ApartmentItemPrototype>()
        .Where(p => p.Price >= 0).OrderBy(p => p.Category).ThenBy(p => p.Price).ThenBy(p => p.ID)
        .Select(p => new ApartmentShopEntry { Id = p.ID, Entity = p.Entity, Name = _prototypes.Index(p.Entity).Name,
            Category = p.Category, Price = p.Price }).ToList();

    private async Task BuyItem(ICommonSession session, ApartmentShopAction ev)
    {
        ApartmentItemOrder order;
        if (ev.Action == "retryItem")
        {
            if (Store.PendingItemOrder(session.UserId.UserId) is not { } pending || pending.Id != ev.Order) return;
            order = pending;
        }
        else
        {
            if (ev.Order == Guid.Empty || !_prototypes.TryIndex<ApartmentItemPrototype>(ev.Template, out var item) ||
                item.Price < 0 || ev.UnitPrice != item.Price)
            { PublishShop(session, "Цена изменилась или предмет недоступен. Проверьте каталог."); return; }
            order = Store.BeginItemOrder(session.UserId.UserId, ev.Order, item.ID, item.Price);
        }
        if (order.Delivered) { PublishShop(session, "Этот заказ уже доставлен."); return; }
        var product = _prototypes.Index<ApartmentItemPrototype>(order.Product);
        // Validate the destination and entity before charging. The same receipt is reused
        // after any ambiguous database response or an interrupted snapshot write.
        _prototypes.Index(product.Entity);
        var instance = InLobby(session) ? PrepareElevatorApartment(session.UserId) : _visits[session.UserId].Apartment;
        LagoonEconomySnapshot result;
        try
        {
            result = await _economyDatabase.PurchaseEconomyAsync(session.UserId, new LagoonEconomyPurchase(
                "apartment-item:" + session.UserId.UserId.ToString("N") + ":" + order.Id.ToString("N"), "apartment-item", product.ID, order.Price, _economy.ExternalTier(session)));
        }
        catch (InsufficientLagoonCoinsException)
        {
            Store.CancelItemOrder(session.UserId.UserId, order.Id);
            throw;
        }
        _economy.Remember(session, result);
        _coins.NotifyEconomyOperation(session.UserId, result, "Предмет для квартиры: " + _prototypes.Index(product.Entity).Name);
        if (!CanManage(session) || !Exists(instance.Grid) || _instances.GetValueOrDefault(session.UserId) != instance)
            return; // Persisted pending order remains available on reopening the shop.
        var entity = Spawn(product.Entity, new EntityCoordinates(instance.Grid,
            new System.Numerics.Vector2(instance.Template.Arrival.X + .5f, instance.Template.Arrival.Y + .5f)));
        if (!SaveApartment(instance, delivery: order.Id))
        {
            Del(entity);
            throw new InvalidOperationException("Оплата учтена, доставка ожидает сохранения. Нажмите «Завершить доставку»; повторного списания не будет.");
        }
        if (_shopTokens.GetValueOrDefault(session.UserId) == ev.Token)
            PublishShop(session, "Предмет доставлен ко входу в вашу квартиру.");
    }
}
