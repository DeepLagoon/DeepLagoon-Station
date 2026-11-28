using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Database;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    private readonly HashSet<NetUserId> _furnitureBusy = new();
    private readonly Dictionary<NetUserId, (Guid Token, TimeSpan At)> _commerceRequests = new();

    private Instance? CommerceEditor(ICommonSession session, Guid token)
    {
        if (!_editors.TryGetValue(session.UserId, out var current) || current != token || !CanManage(session)) return null;
        var instance = InLobby(session) ? _lobbyEditors.GetValueOrDefault(session.UserId) : _visits.GetValueOrDefault(session.UserId)?.Apartment;
        if (instance == null || instance.Owner != session.UserId ||
            Store.Load(session.UserId.UserId) is { } selected && selected.Template != instance.Template.ID) return null;
        return instance;
    }

    private IEnumerable<ApartmentFurniturePrototype> ProductVariants(string product) =>
        _prototypes.EnumeratePrototypes<ApartmentFurniturePrototype>().Where(item => !item.ExistingOnly && item.StockKey == product);

    private Dictionary<string, int> LegacyFurniture(Instance instance) => instance.Furniture.Values
        .Select(uid => TryComp<ApartmentFurnitureIdentityComponent>(uid, out var identity) && !identity.PlacedWithPurchase
            ? Catalog(identity.Furniture)
            : null)
        .OfType<ApartmentFurniturePrototype>()
        .GroupBy(item => item.StockKey)
        .ToDictionary(group => group.Key, group => group.Count());

    private int FurnitureCapacity(string product, LagoonEconomySnapshot? account, int legacyPlacements)
    {
        if (Catalog(product) is not { } model || model.StockKey != model.ID) return 0;
        return ApartmentFurnitureStock.Capacity(model, ProductVariants(product),
            account?.State.FurnitureStock ?? new Dictionary<string, int>(), account?.State.Furniture ?? new HashSet<string>(),
            legacyPlacements);
    }

    private List<ApartmentProduct> FurnitureProducts(Instance instance, LagoonEconomySnapshot account)
    {
        var tier = Math.Max(account.State.ExternalTier, account.State.PassTicks > 0 ? account.State.PassTier : 0);
        var legacy = LegacyFurniture(instance);
        return _prototypes.EnumeratePrototypes<ApartmentFurniturePrototype>()
            .Where(item => item.IsForSale).GroupBy(item => item.StockKey)
            .Select(group =>
            {
                var product = Catalog(group.Key);
                if (product == null || !product.IsForSale || product.StockKey != product.ID)
                    return null;
                return new ApartmentProduct { Id = product.ID, Name = Loc.GetString(product.Name), Category = product.Category,
                    PackSize = product.MaxCount, Stock = ApartmentFurnitureStock.Capacity(product, group,
                        account.State.FurnitureStock, account.State.Furniture, legacy.GetValueOrDefault(product.ID)),
                    Price = LagoonEconomyRules.DecorPrice(product.Price, tier),
                    Variants = group.OrderBy(item => item.ID == product.ID ? 0 : 1).ThenBy(item => item.ID).Select(item => item.ID).ToArray() };
            }).OfType<ApartmentProduct>().OrderBy(item => item.Category).ThenBy(item => item.Price).ThenBy(item => item.Id).ToList();
    }

    private void PublishCommerce(ICommonSession session, Guid token, LagoonEconomySnapshot account, Guid order = default,
        bool success = false, string message = "", bool completed = true)
    {
        if (!session.Channel.IsConnected || CommerceEditor(session, token) is not { } instance) return;
        RaiseNetworkEvent(new ApartmentCommerceStateEvent(token, account.Balance, FurnitureProducts(instance, account), order, success, message, completed), session);
    }

    private async Task CommerceFailure(ICommonSession session, Guid token, Guid order, string message, bool completed)
    {
        try { PublishCommerce(session, token, await _economy.Current(session), order, message: message, completed: completed); }
        catch (Exception e)
        {
            Log.Warning($"Apartment commerce refresh failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}");
            if (session.Channel.IsConnected && CommerceEditor(session, token) != null)
                RaiseNetworkEvent(new ApartmentCommerceStateEvent(token, -1, [], order, message: message, completed: completed), session);
        }
    }

    private async void OnCommerceRequest(ApartmentCommerceRequest ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (CommerceEditor(session, ev.Token) == null || _commerceRequests.TryGetValue(session.UserId, out var last) &&
            last.Token == ev.Token && _timing.RealTime - last.At < TimeSpan.FromSeconds(1) || !_furnitureBusy.Add(session.UserId)) return;
        _commerceRequests[session.UserId] = (ev.Token, _timing.RealTime);
        try { PublishCommerce(session, ev.Token, await _economy.Current(session)); }
        catch (Exception e)
        {
            Log.Warning($"Apartment catalogue failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}");
            if (session.Channel.IsConnected && CommerceEditor(session, ev.Token) != null)
                RaiseNetworkEvent(new ApartmentCommerceStateEvent(ev.Token, -1, [], message: "Не удалось загрузить баланс и запас. Повторите загрузку."), session);
        }
        finally { _furnitureBusy.Remove(session.UserId); }
    }

    private async void OnFurniturePurchase(ApartmentFurniturePurchaseEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (CommerceEditor(session, ev.Token) == null || ev.Order == Guid.Empty ||
            ev.Product is not { Length: > 0 and <= 128 } || ev.Quantity < 1 || ev.UnitPrice is < 1 or > 500) return;
        if (!_furnitureBusy.Add(session.UserId)) return;
        try
        {
            var product = Catalog(ev.Product);
            if (product == null || !product.IsForSale || product.StockKey != product.ID)
                throw new ArgumentException("Этот набор отсутствует в каталоге квартиры.");
            var purchase = new LagoonEconomyPurchase("furniture-pack:" + session.UserId.UserId.ToString("N") + ":" + ev.Order.ToString("N"),
                "furniture-pack", product.ID, product.Price, _economy.ExternalTier(session), ev.Quantity, product.MaxCount, ev.UnitPrice);
            var result = await _economyDatabase.PurchaseEconomyAsync(session.UserId, purchase);
            _economy.Remember(session, result);
            _coins.NotifyEconomyOperation(session.UserId, result, "Наборы декора: " + Loc.GetString(product.Name));
            PublishCommerce(session, ev.Token, result, ev.Order, true, "Наборы добавлены в каталог обустройства.");
        }
        catch (InsufficientLagoonCoinsException) { await CommerceFailure(session, ev.Token, ev.Order, "Недостаточно Lagoon Coin.", true); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        { await CommerceFailure(session, ev.Token, ev.Order, e is OverflowException ? "Слишком большое количество наборов." : e.Message, true); }
        catch (Exception e)
        {
            Log.Error($"Apartment furniture purchase failed: {Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}");
            await CommerceFailure(session, ev.Token, ev.Order, "Не удалось проверить результат. Повторите тот же запрос; повторного списания не будет.", false);
        }
        finally { _furnitureBusy.Remove(session.UserId); }
    }
}
