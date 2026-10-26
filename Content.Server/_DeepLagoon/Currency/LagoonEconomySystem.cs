using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Currency;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Database;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;
using System.Text.Json;

namespace Content.Server._DeepLagoon.Currency;

public sealed partial class LagoonEconomySystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedDiscordBoostSystem _supporters = default!;
    private readonly Dictionary<NetUserId, (LagoonEconomySnapshot Snapshot, DateTime At)> _cache = new();
    private readonly HashSet<NetUserId> _loading = new(), _busy = new();
    private readonly Dictionary<NetUserId, Guid> _tokens = new();
    private readonly Dictionary<(NetUserId User, bool Supporters), DateTime> _requests = new();
    private readonly Dictionary<NetUserId, Guid> _menuRequests = new();
    private DateTime _nextRefresh;
    private string[] _paidNames = [];
    private DateTime _paidRefresh;
    private bool _paidLoading;

    public override void Initialize()
    {
        SubscribeNetworkEvent<LagoonEconomyRequest>((ev, args) => Open(args.SenderSession, ev.Request));
        SubscribeNetworkEvent<LagoonEconomyAction>(Action);
        SubscribeNetworkEvent<LagoonPaidSupportersRequest>((_, args) => PaidSupporters(args.SenderSession));
        _players.PlayerStatusChanged += OnStatus;
    }
    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnStatus;
        _cache.Clear(); _tokens.Clear(); _requests.Clear(); _menuRequests.Clear();
        base.Shutdown();
    }
    private void OnStatus(object? sender, SessionStatusEventArgs ev)
    {
        if (ev.NewStatus != Robust.Shared.Enums.SessionStatus.Disconnected) return;
        _cache.Remove(ev.Session.UserId); _tokens.Remove(ev.Session.UserId); _menuRequests.Remove(ev.Session.UserId);
        _requests.Remove((ev.Session.UserId, false)); _requests.Remove((ev.Session.UserId, true));
    }
    public int ExternalTier(ICommonSession session) => EntityManager.System<Content.Server._DeepLagoon.DiscordLink.DiscordLinkSystem>().RawProductTier(session);
    public int CachedExternalTier(ICommonSession? session) => session != null && _cache.TryGetValue(session.UserId, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2) ? (cached.Snapshot.State.ExternalExpiresAt is not { } expires || expires > DateTime.UtcNow ? cached.Snapshot.State.ExternalTier : 0) : 0;
    public (int Tier, int Seconds) PurchasedStatus(ICommonSession? session)
    {
        if (session == null || !_cache.TryGetValue(session.UserId, out var cached) || DateTime.UtcNow - cached.At > TimeSpan.FromMinutes(2)) return (0, 0);
        var state = cached.Snapshot.State;
        var ticks = state.PassTicks;
        if (state.ExternalTier < state.PassTier)
            ticks = Math.Max(0, ticks - (DateTime.UtcNow - cached.At).Ticks);
        else if (state.ExternalExpiresAt is { } expiry && expiry < DateTime.UtcNow)
            ticks = Math.Max(0, ticks - (DateTime.UtcNow - (expiry > cached.At ? expiry : cached.At)).Ticks);
        return ticks > 0 ? (state.PassTier, (int)Math.Min(900, Math.Max(0, ticks / TimeSpan.TicksPerSecond))) : (0, 0);
    }
    public int PurchasedTier(ICommonSession? session) => PurchasedStatus(session).Tier;
    public string? OocColor(ICommonSession session) => _supporters.HasOocColorAccess(session) && _cache.TryGetValue(session.UserId, out var cached) ? cached.Snapshot.State.OocColor : null;
    public bool OwnsFurniture(NetUserId user, string furniture) => _cache.TryGetValue(user, out var cached) && cached.Snapshot.State.Furniture.Contains(furniture);
    public void Remember(ICommonSession session, LagoonEconomySnapshot snapshot)
    {
        if (!session.Channel.IsConnected) return;
        if (_cache.TryGetValue(session.UserId, out var previous) && previous.Snapshot.State.Revision > snapshot.State.Revision) return;
        _cache[session.UserId] = (snapshot, DateTime.UtcNow);
        RaiseNetworkEvent(new LagoonCoinBalanceEvent(snapshot.Balance), session);
    }
    public async Task<LagoonEconomySnapshot> Current(ICommonSession session)
    {
        var snapshot = await _database.GetEconomyAsync(session.UserId, ExternalTier(session));
        Remember(session, snapshot); return snapshot;
    }
    public LagoonEconomySnapshot? Cached(NetUserId user) => _cache.TryGetValue(user, out var cached) ? cached.Snapshot : null;
    public override void Update(float frameTime)
    {
        if (DateTime.UtcNow < _nextRefresh) return;
        _nextRefresh = DateTime.UtcNow.AddSeconds(30);
        foreach (var session in _players.Sessions) Refresh(session);
    }
    public async void Refresh(ICommonSession session)
    {
        if (!session.Channel.IsConnected || session.Channel.AuthType != LoginType.LoggedIn || !_loading.Add(session.UserId)) return;
        try
        {
            var snapshot = await _database.GetEconomyAsync(session.UserId, ExternalTier(session));
            Remember(session, snapshot);
        }
        catch (Exception e) { Log.Warning($"Economy refresh failed: {e.GetType().Name}"); }
        finally { _loading.Remove(session.UserId); }
    }
    private bool CanRequest(ICommonSession session, bool supporters = false)
    {
        var key = (session.UserId, supporters);
        if (session.Channel.AuthType != LoginType.LoggedIn || _requests.TryGetValue(key, out var previous) && DateTime.UtcNow - previous < TimeSpan.FromSeconds(1)) return false;
        _requests[key] = DateTime.UtcNow;
        return true;
    }
    private void MenuError(ICommonSession session, Guid request, string message)
    {
        if (session.Channel.IsConnected)
            RaiseNetworkEvent(new LagoonEconomyStateEvent(Guid.Empty, JsonSerializer.Serialize(new { error = message }), request), session);
    }
    private async void Open(ICommonSession session, Guid request)
    {
        if (request == Guid.Empty) return;
        if (session.Channel.AuthType != LoginType.LoggedIn)
        {
            MenuError(session, request, "Для доступа к Lagoon Coin войдите в учётную запись.");
            return;
        }
        if (!CanRequest(session) || !_busy.Add(session.UserId))
        {
            MenuError(session, request, "Предыдущий запрос ещё обрабатывается. Повторите попытку через несколько секунд.");
            return;
        }
        _menuRequests[session.UserId] = request;
        try { await Publish(session); }
        catch (Exception e)
        {
            Log.Warning($"Economy menu failed: {e.GetType().Name}");
            MenuError(session, request, "Не удалось загрузить данные Lagoon Coin. Попробуйте ещё раз.");
        }
        finally { _busy.Remove(session.UserId); }
    }
    private async Task Publish(ICommonSession session, string message = "")
    {
        var snapshot = await _database.GetEconomyAsync(session.UserId, ExternalTier(session));
        if (!session.Channel.IsConnected) return;
        Remember(session, snapshot);
        var token = Guid.NewGuid(); _tokens[session.UserId] = token;
        var state = snapshot.State;
        var tier = _supporters.GetProductTier(session);
        var data = JsonSerializer.Serialize(new { balance = snapshot.Balance, message, tier, color = state.OocColor ?? "#7ABFA5",
            referralCode = state.ReferralCode, referralStage = state.ReferralStage,
            passTier = state.PassTier, passDays = state.PassTicks / (double) TimeSpan.TicksPerDay,
            paused = state.ExternalTier >= state.PassTier && state.PassTier > 0,
            subscriptions = Enumerable.Range(1, 4).Select(t => new { id = t, name = LagoonEconomyRules.Name(t), price = LagoonEconomyRules.PassPrice(t), rate = LagoonEconomyRules.HourlyRate(t) }) });
        RaiseNetworkEvent(new LagoonEconomyStateEvent(token, data, _menuRequests.GetValueOrDefault(session.UserId)), session);
    }
    private async void Action(LagoonEconomyAction ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_tokens.TryGetValue(session.UserId, out var token) || token != ev.Token || ev.Order == Guid.Empty || ev.Product.Length > 128 || !_busy.Add(session.UserId)) return;
        try
        {
            switch (ev.Action)
            {
                case "pass":
                    var purchase = await _database.PurchaseEconomyAsync(session.UserId, new LagoonEconomyPurchase("game:" + ev.Order.ToString("N"), "pass", ev.Product, 0, ExternalTier(session)));
                    Remember(session, purchase);
                    EntityManager.System<LagoonCoinSystem>().NotifyEconomyOperation(session.UserId, purchase, "Покупка игрового доступа");
                    break;
                case "referral": await _database.BindReferralAsync(session.UserId, ev.Product); break;
                case "color": await _database.SetEconomyColorAsync(session.UserId, ev.Product, ExternalTier(session), "color:game:" + ev.Order.ToString("N")); break;
                default: return;
            }
            await Publish(session, "Изменения сохранены.");
        }
        catch (InsufficientLagoonCoinsException) { await Publish(session, "Недостаточно Lagoon Coin."); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { await Publish(session, e.Message); }
        catch (Exception e) { Log.Error($"Economy action failed: {e.GetType().Name}"); MenuError(session, _menuRequests.GetValueOrDefault(session.UserId), "Не удалось выполнить операцию. Загрузите данные заново, чтобы проверить её результат."); }
        finally { _busy.Remove(session.UserId); }
    }
    private async void PaidSupporters(ICommonSession session)
    {
        if (!CanRequest(session, supporters: true)) return;
        try
        {
            if (DateTime.UtcNow >= _paidRefresh && !_paidLoading)
            {
                _paidLoading = true;
                try
                {
                    _paidNames = (await _database.GetPaidSupportersAsync()).Select(p => p.Name).ToArray();
                    _paidRefresh = DateTime.UtcNow.AddSeconds(30);
                }
                finally { _paidLoading = false; }
            }
            if (session.Channel.IsConnected) RaiseNetworkEvent(new LagoonPaidSupportersEvent(_paidNames), session);
        }
        catch (Exception e) { Log.Warning($"Paid supporter list failed: {e.GetType().Name}"); }
    }
}
