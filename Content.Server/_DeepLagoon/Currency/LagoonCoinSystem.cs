using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Content.Server.Afk;
using Content.Server.Afk.Events;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.Objectives;
using Content.Shared._DeepLagoon.Currency;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Content.Shared.Players;
using System.Threading;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Currency;

/// <summary>A separate donation wallet. Gameplay bank deposits never reach this system.</summary>
public sealed partial class LagoonCoinSystem : EntitySystem
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedDiscordBoostSystem _supporters = default!;
    [Dependency] private SharedRoleSystem _roles = default!;
    [Dependency] private ObjectivesSystem _objectives = default!;
    [Dependency] private Content.Server.Chat.Managers.IChatManager _chat = default!;

    private readonly HashSet<NetUserId> _ready = new();
    private readonly HashSet<NetUserId> _spawned = new();
    private readonly HashSet<NetUserId> _readyCandidates = new();
    private string _rewardRound = "";
    private sealed record ActivityBucket(string Day, int Rate, string Round, bool Ready);
    private readonly Dictionary<ICommonSession, Participation> _participation = new();
    private readonly Queue<Reward> _pending = new();
    private readonly Dictionary<NetUserId, TimeSpan> _requests = new();
    private readonly string _run = Guid.NewGuid().ToString("N");
    private long _sequence;
    private bool _draining;
    private bool _roundEnded;
    private TimeSpan _nextSave;
    private TimeSpan _retryAt;
    private Task _drainTask = Task.CompletedTask;
    private LagoonCoinOutbox _outbox = default!;
    private readonly SemaphoreSlim _walletOperations = new(1, 1);

    private sealed class Participation
    {
        public TimeSpan Last;
        public bool Active;
        public bool Subscribed;
        public long Played;
        public long Bonus;
        public int Rate = 2;
        public readonly Dictionary<ActivityBucket, long> Buckets = new();
    }

    private sealed record Reward(NetUserId User, string Key, long Amount, string Reason,
        NetUserId? Actor = null, long Played = 0, long Bonus = 0, LagoonActivity? Activity = null);

    public override void Initialize()
    {
        base.Initialize();
        var root = _resources.UserData.RootDir;
        // Virtual in-memory user data is used by the integration test server.
        _outbox = new LagoonCoinOutbox(root == null ? ":memory:" : Path.Combine(root, "lagoon-coin-outbox.db"));
        foreach (var saved in _outbox.Load())
            _pending.Enqueue(new Reward(new NetUserId(saved.User), saved.Key, saved.Amount, saved.Reason,
                saved.Actor == null ? null : new NetUserId(saved.Actor.Value), saved.Played, saved.Bonus, saved.Activity));
        SubscribeNetworkEvent<LagoonCoinBalanceRequest>(OnBalanceRequest);
        SubscribeLocalEvent<RoundStartingEvent>(_ => _spawned.Clear());
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        SubscribeLocalEvent<PlayerAttachedEvent>(ev => Accumulate(ev.Player));
        SubscribeLocalEvent<PlayerDetachedEvent>(ev =>
        {
            Accumulate(ev.Player);
            _participation[ev.Player].Active = false;
        });
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(ev =>
        {
            Accumulate(ev.PlayerSession);
            _participation[ev.PlayerSession].Active = false;
        });
        SubscribeLocalEvent<MobStateChangedEvent>(ev =>
        {
            if (TryComp<ActorComponent>(ev.Target, out var actor)) Accumulate(actor.PlayerSession);
        });
        SubscribeLocalEvent<AFKEvent>(OnAfk);
        SubscribeLocalEvent<UnAFKEvent>(OnUnAfk);
        SubscribeLocalEvent<RoundStartedEvent>(OnStarted);
        SubscribeLocalEvent<RoundEndedEvent>(OnEnded);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            SaveParticipation();
            _ready.Clear();
            _spawned.Clear();
        });
        _players.PlayerStatusChanged += OnStatusChanged;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnStatusChanged;
        SaveParticipation();
        _tasks.BlockWaitOnTask(_drainTask);
        _drainTask = Drain();
        _tasks.BlockWaitOnTask(_drainTask);
        if (_pending.Count != 0)
            Log.Warning($"{_pending.Count} Lagoon Coin rewards remain in the outbox for the next server start.");
        _outbox.Dispose();
        base.Shutdown();
    }

    public void SetReady(ICommonSession session, bool ready)
    {
        if (ready) _ready.Add(session.UserId);
        else _ready.Remove(session.UserId);
    }

    private void OnSpawn(PlayerSpawnCompleteEvent ev)
    {
        if (!ev.LateJoin && !ev.Silent && _ready.Contains(ev.Player.UserId))
            _spawned.Add(ev.Player.UserId);
    }

    private void OnStarted(RoundStartedEvent ev)
    {
        _roundEnded = false;
        _rewardRound = ev.RoundId.ToString();
        _readyCandidates.Clear();
        _readyCandidates.UnionWith(_spawned);
        _spawned.Clear();
    }

    private void OnEnded(RoundEndedEvent ev)
    {
        SaveParticipation();
        _roundEnded = true;
        foreach (var data in _participation.Values) data.Active = false;
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindId, out var mind))
        {
            if (!_roles.MindIsAntagonist(mindId) || (mind.UserId ?? mind.OriginalOwnerUserId) is not { } user)
                continue;
            foreach (var objective in mind.Objectives.Distinct())
            {
                if (!Exists(objective) || !_objectives.IsCompleted(objective))
                    continue;
                Queue(new Reward(user, $"round:{ev.RoundId}:objective:{objective}",
                    LagoonCoinRules.ObjectiveReward, "antagonist-objective"));
            }
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var session in _players.Sessions)
            Accumulate(session);
        if (_timing.RealTime >= _nextSave)
        {
            _nextSave = _timing.RealTime + TimeSpan.FromMinutes(1);
            SaveParticipation();
        }
        if (!_draining && _pending.Count > 0 && _timing.RealTime >= _retryAt)
            _drainTask = Drain();
    }

    private void Accumulate(ICommonSession session)
    {
        var now = _timing.RealTime;
        if (!_participation.TryGetValue(session, out var data))
            _participation[session] = data = new Participation { Last = now };
        var elapsed = Math.Max(0, (now - data.Last).Ticks);
        if (data.Active)
        {
            var start = DateTime.UtcNow.AddTicks(-elapsed);
            while (elapsed > 0)
            {
                var boundary = start.AddHours(3).Date.AddDays(1).AddHours(-3);
                var ticks = Math.Min(elapsed, Math.Max(1, (boundary - start).Ticks));
                var bucket = new ActivityBucket(Content.Shared.Database.LagoonEconomyRules.MoscowDay(start), data.Rate, _rewardRound, _readyCandidates.Contains(session.UserId));
                data.Buckets[bucket] = checked(data.Buckets.GetValueOrDefault(bucket) + ticks);
                start = start.AddTicks(ticks);
                elapsed -= ticks;
            }
        }
        data.Last = now;
        var attached = session.AttachedEntity;
        data.Active = LagoonCoinRules.CountsTime(_ticker.RunLevel == GameRunLevel.InRound && !_roundEnded,
            session.Status == SessionStatus.InGame, attached != null,
            HasComp<GhostComponent>(attached) || HasComp<Content.Shared._DeepLagoon.Apartments.ApartmentResidentComponent>(attached),
            TryComp<MobStateComponent>(attached, out var mob) && mob.CurrentState is MobState.Alive or MobState.Critical,
            _afk.IsAfk(session));
        data.Rate = Content.Shared.Database.LagoonEconomyRules.HourlyRate(_supporters.GetProductTier(session));
    }

    private void SaveParticipation()
    {
        foreach (var (session, data) in _participation.ToArray())
        {
            Accumulate(session);
            Flush(session, data);
        }
    }

    private void Flush(ICommonSession session, Participation data)
    {
        foreach (var (bucket, ticks) in data.Buckets)
        {
            var key = $"time:{_run}:{++_sequence}";
            var activity = new LagoonActivity(key, ticks, bucket.Rate, bucket.Day, bucket.Round, bucket.Ready);
            Queue(new Reward(session.UserId, key, 0, "playtime", Activity: activity));
        }
        data.Buckets.Clear();
    }

    private void OnStatusChanged(object? sender, SessionStatusEventArgs ev)
    {
        if (ev.NewStatus != SessionStatus.Disconnected) return;
        Accumulate(ev.Session);
        if (_participation.Remove(ev.Session, out var data)) Flush(ev.Session, data);
        _requests.Remove(ev.Session.UserId);
        _ready.Remove(ev.Session.UserId);
    }

    private void OnAfk(ref AFKEvent ev)
    {
        Accumulate(ev.Session);
        _participation[ev.Session].Active = false;
    }
    private void OnUnAfk(ref UnAFKEvent ev) => Accumulate(ev.Session);

    private static PendingLagoonCoinReward Pending(Reward reward)
        => new(reward.User.UserId, reward.Key, reward.Amount, reward.Reason, reward.Actor?.UserId, reward.Played, reward.Bonus, reward.Activity);

    private void Queue(Reward reward)
    {
        if (_outbox.Store(Pending(reward))) _pending.Enqueue(reward);
    }

    private async Task Drain()
    {
        if (_draining) return;
        _draining = true;
        try
        {
            var count = _pending.Count;
            while (count-- > 0 && _pending.TryDequeue(out var reward))
            {
                try
                {
                    await _walletOperations.WaitAsync();
                    try
                    {
                        var result = reward.Activity != null
                            ? await _db.RecordActivityAsync(reward.User, reward.Activity)
                            : await _db.AwardLagoonCoinsAsync(reward.User, reward.Key, reward.Amount,
                                reward.Reason, reward.Actor, reward.Played, reward.Bonus);
                        SendBalance(reward.User, result.Balance);
                        NotifyOperation(reward.User, result, reward.Reason);
                        if (reward.Activity != null)
                        {
                            foreach (var referral in await _db.ReferralRewardsAsync(reward.User))
                            {
                                var recipient = new NetUserId(referral.User);
                                SendBalance(recipient, referral.Result.Balance);
                                NotifyOperation(recipient, referral.Result, "Реферальная награда");
                            }
                        }
                        _outbox.Complete(Pending(reward));
                    }
                    finally { _walletOperations.Release(); }
                }
                catch (Exception e)
                {
                    Log.Error($"Lagoon Coin reward {reward.Key} not committed; retrying the same key: {e}");
                    _retryAt = _timing.RealTime + TimeSpan.FromSeconds(30);
                    _pending.Enqueue(reward);
                }
            }
        }
        finally { _draining = false; }
    }

    public async Task<LagoonCoinResult> Grant(NetUserId user, long amount, string reason, NetUserId? actor, string? operationId = null)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return await ChangeBalance(user, amount, reason, actor, operationId);
    }

    public async Task<LagoonCoinResult> Deduct(NetUserId user, long amount, string reason, NetUserId? actor, string operationId)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return await ChangeBalance(user, -amount, reason, actor, operationId);
    }

    /// <summary>Future payment backend calls this only after verifying the external payment, using its stable order ID.</summary>
    public async Task<LagoonCoinResult> RecordPurchase(NetUserId user, long amount, string orderId, string reason)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(orderId) || orderId.Length > 180)
            throw new ArgumentException("Invalid verified LC purchase.");
        return await ChangeBalance(user, amount, reason, null, $"purchase:{orderId}");
    }

    /// <summary>Future shop confirms this debit before issuing goods. Reuse the same order ID on retries.</summary>
    public async Task<LagoonCoinResult> Spend(NetUserId user, long amount, string orderId, string reason)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(orderId) || orderId.Length > 180)
            throw new ArgumentException("Invalid LC spending operation.");
        return await ChangeBalance(user, -amount, reason, user, $"spend:{orderId}");
    }

    private async Task<LagoonCoinResult> ChangeBalance(NetUserId user, long amount, string reason, NetUserId? actor, string? operationId)
    {
        await _walletOperations.WaitAsync();
        try
        {
            var result = await _db.AwardLagoonCoinsAsync(user, operationId ?? $"admin:{Guid.NewGuid():N}", amount, reason, actor);
            SendBalance(user, result.Balance);
            NotifyOperation(user, result, reason);
            return result;
        }
        finally { _walletOperations.Release(); }
    }

    private void SendBalance(NetUserId user, long balance)
    {
        if (_players.TryGetSessionById(user, out var session) && session.Channel.IsConnected)
            RaiseNetworkEvent(new LagoonCoinBalanceEvent(balance), session);
    }

    public void NotifyEconomyOperation(NetUserId user, LagoonEconomySnapshot purchase, string reason)
    {
        SendBalance(user, purchase.Balance);
        NotifyOperation(user, new LagoonCoinResult(purchase.Balance, purchase.Awarded, purchase.Applied), reason);
    }
    private void NotifyOperation(NetUserId user, LagoonCoinResult result, string reason)
    {
        if (!result.Applied || result.Awarded == 0 ||
            !_players.TryGetSessionById(user, out var session) ||
            session.Status != SessionStatus.InGame || !session.Channel.IsConnected)
            return;

        var description = reason switch
        {
            "ready" => Loc.GetString("lagoon-coin-reason-ready"),
            "playtime" => Loc.GetString("lagoon-coin-reason-playtime"),
            "antagonist-objective" => Loc.GetString("lagoon-coin-reason-objective"),
            _ => reason,
        };
        var message = result.Awarded > 0 ? "lagoon-coin-received" : "lagoon-coin-deducted";
        _chat.DispatchServerMessage(session, Loc.GetString(message,
            ("amount", result.Awarded), ("balance", result.Balance), ("reason", description)), suppressLog: true);
        RaiseNetworkEvent(new LagoonCoinRewardEvent(result.Awarded, result.Balance, reason), session);
    }

    private async void OnBalanceRequest(LagoonCoinBalanceRequest ev, EntitySessionEventArgs args)
    {
        var user = args.SenderSession.UserId;
        if (_requests.TryGetValue(user, out var last) && _timing.RealTime - last < TimeSpan.FromSeconds(1)) return;
        _requests[user] = _timing.RealTime;
        await _walletOperations.WaitAsync();
        try { SendBalance(user, await _db.GetLagoonCoinsAsync(user)); }
        catch (Exception e) { Log.Error($"Could not fetch Lagoon Coin balance for {user}: {e}"); }
        finally { _walletOperations.Release(); }
    }
}
