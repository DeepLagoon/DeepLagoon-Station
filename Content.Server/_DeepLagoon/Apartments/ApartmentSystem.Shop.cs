using System.Linq;
using Content.Server.GameTicking;
using Content.Server._DeepLagoon.Currency;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.GameTicking;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private LagoonCoinSystem _coins = default!;
    private readonly Dictionary<NetUserId, Guid> _shopTokens = new();
    private readonly Dictionary<NetUserId, Instance> _lobbyEditors = new();
    private readonly List<Instance> _retiring = new();
    private readonly HashSet<NetUserId> _shopping = new();
    private bool InLobby(ICommonSession session) => _ticker.PlayerGameStatuses.TryGetValue(session.UserId, out var state) && state != PlayerGameStatus.JoinedGame;
    private bool CanManage(ICommonSession session) => Enabled && session.Channel.IsConnected && (InLobby(session) ||
        _visits.TryGetValue(session.UserId, out var visit) && visit.Apartment.Owner == session.UserId && session.AttachedEntity == visit.Body &&
        Transform(visit.Body).GridUid == visit.Apartment.Grid && !HasComp<ApartmentTripPendingComponent>(visit.Body));
    private bool Available(ApartmentTemplatePrototype template) => template.Available && template.Map != null && template.Price >= 0;
    private ApartmentDelta? SelectedLayout(NetUserId owner)
    {
        var saved = Store.Load(owner.UserId);
        if (saved == null && _instances.TryGetValue(owner, out var old) && !Available(old.Template))
        {
            saved = ApartmentLayout.Diff(old.Template, CurrentLayout(old), 1, MapBaseline(old));
            if (!Store.Save(owner.UserId, saved, 0)) saved = Store.Load(owner.UserId);
        }
        if (saved != null && (!_prototypes.TryIndex<ApartmentTemplatePrototype>(saved.Template, out var previous) || !Available(previous)))
        {
            var fallback = _prototypes.Index<ApartmentTemplatePrototype>(_config.GetCVar(ApartmentCVars.DefaultTemplate));
            if (!Available(fallback) || fallback.Price != 0) throw new InvalidOperationException("Default apartment must be available and free.");
            saved = Store.Select(owner.UserId, fallback);
            _numbers.Rotate(owner.UserId);
        }
        return saved;
    }
    private void RefreshSelectedInstance(NetUserId owner)
    {
        var saved = SelectedLayout(owner);
        if (saved == null) return;
        _templates[owner] = saved.Template;
        if (!_instances.TryGetValue(owner, out var instance) || instance.Template.ID == saved.Template) return;
        _instances.Remove(owner);
        if (Visitors(instance).Length > 0 || HasComp<ApartmentElevatorReservedComponent>(instance.Grid)) _retiring.Add(instance);
        else Del(instance.Grid);
    }
    private void OpenShop(ApartmentShopRequest ev, EntitySessionEventArgs args)
    {
        if (CanManage(args.SenderSession)) PublishShop(args.SenderSession);
    }
    private void PublishShop(ICommonSession session, string message = "")
    {
        var selected = SelectedLayout(session.UserId)?.Template ?? _config.GetCVar(ApartmentCVars.DefaultTemplate);
        var token = Guid.NewGuid(); _shopTokens[session.UserId] = token;
        var entries = _prototypes.EnumeratePrototypes<ApartmentTemplatePrototype>().Where(Available).OrderBy(p => p.Price).ThenBy(p => p.ID)
            .Select(p => new ApartmentShopEntry { Id = p.ID, Name = Loc.GetString(p.Name), Price = p.Price,
                Owned = p.Price == 0 || Store.Owned(session.UserId.UserId, p.ID), Selected = selected == p.ID }).ToList();
        var legacy = _visits.TryGetValue(session.UserId, out var visit) && visit.Apartment.Template.Map == null;
        RaiseNetworkEvent(new ApartmentShopState(token, entries, Number(session.UserId), InLobby(session), legacy, message), session);
    }
    private async void ShopAction(ApartmentShopAction ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_shopTokens.TryGetValue(session.UserId, out var token) || token != ev.Token) return;
        if (ev.Action == "close") { _shopTokens.Remove(session.UserId); return; }
        if (!CanManage(session) || !_shopping.Add(session.UserId)) return;
        try
        {
            if (ev.Action == "legacyExit")
            {
                if (_visits.TryGetValue(session.UserId, out var old) && old.Apartment.Template.Map == null)
                    EntityManager.System<ApartmentTravelSystem>().ReturnLegacy(session);
                return;
            }
            if (ev.Action == "edit")
            {
                if (InLobby(session)) OpenLobbyEditor(session); else OpenEditor(session);
                return;
            }
            if (ev.Action != "select" || ev.Template is not { Length: > 0 and <= 128 } || !_prototypes.TryIndex<ApartmentTemplatePrototype>(ev.Template, out var template) || !Available(template)) return;
            EnsureMaps();
            var validation = CreateFresh(session.UserId, template.ID, true);
            Del(validation.Grid);
            if (!Store.Owned(session.UserId.UserId, template.ID))
            {
                var purchase = Store.BeginPurchase(session.UserId.UserId, template.ID, template.Price);
                if (purchase.Price > 0)
                    await _coins.Spend(session.UserId, purchase.Price, "apartment:" + purchase.Operation, "apartment-template:" + template.ID);
                Store.Grant(session.UserId.UserId, template.ID);
            }
            if (!CanManage(session) || _shopTokens.GetValueOrDefault(session.UserId) != ev.Token) return;
            var before = SelectedLayout(session.UserId)?.Template ?? _config.GetCVar(ApartmentCVars.DefaultTemplate);
            Store.Select(session.UserId.UserId, template);
            _templates[session.UserId] = template.ID;
            if (before != template.ID) _numbers.Rotate(session.UserId.UserId);
            _editors.Remove(session.UserId); _lobbyEditors.Remove(session.UserId);
            Result(session, true, "Шаблон выбран.", -1);
            PublishShop(session, InLobby(session) ? "Шаблон выбран. Можно изменить обстановку до входа в раунд."
                : $"Шаблон выбран. Спуститесь на остановку и введите новый номер: {Number(session.UserId)}.");
        }
        catch (Content.Server.Database.InsufficientLagoonCoinsException) { if (CanManage(session)) PublishShop(session, "Недостаточно Lagoon Coin."); }
        catch (Exception e) { Log.Error($"Apartment shop operation failed for {session.UserId}: {e}"); if (CanManage(session)) PublishShop(session, "Операция не завершена. Повторите запрос; повторного списания за этот шаблон не будет."); }
        finally { _shopping.Remove(session.UserId); }
    }
    private void OpenLobbyEditor(ICommonSession session)
    {
        if (!InLobby(session)) return;
        var instance = PrepareElevatorApartment(session.UserId);
        var token = Guid.NewGuid(); _editors[session.UserId] = token; _lobbyEditors[session.UserId] = instance;
        RaiseNetworkEvent(new ApartmentEditorEvent(GetNetEntity(instance.Grid), instance.Template.ID, instance.Delta.Revision, token,
            CurrentLayout(instance), new(), CaptureRoom(instance), Number(session.UserId), instance.MapFurniture.Values.ToList(), true), session);
    }
}
