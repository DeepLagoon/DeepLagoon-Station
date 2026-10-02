using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Server.Chat.Managers;
using Content.Shared.Chat;
using Robust.Shared.Utility;
using System.Linq;
using System.Numerics;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.Verbs;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem : EntitySystem
{
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly InteractionPanelManaSystem _mana = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, Panel> _panels = new();
    private readonly Dictionary<(EntityUid User, InteractionPanelCategory Category), TimeSpan> _cooldowns = new();
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly Dictionary<(EntityUid User, InteractionPanelCategory Category), Repeat> _repeats = new();
    private TimeSpan _nextUpdate;
    private readonly InteractionPanelAskLimiter _askLimiter = new();
    private readonly Dictionary<Guid, (Guid Sender, Guid Recipient, TimeSpan Expires)> _askHistory = new();
    private sealed record Panel(EntityUid Target, ICommonSession Session, TimeSpan Expires);
    private sealed record Pending(Guid Token, EntityUid User, EntityUid Target, InteractionPanelCategory Category, ICommonSession Session, ICommonSession Recipient,
        string Action, TimeSpan Expires);
    private sealed class Repeat(string action, float interval, TimeSpan next)
    {
        public string Action = action;
        public float Interval = interval;
        public TimeSpan Next = next;
    }

    public override void Initialize()
    {
        SubscribeLocalEvent<MobStateComponent, ComponentStartup>(OnMobStartup);
        SubscribeLocalEvent<HumanoidAppearanceComponent, InteractionPanelProfileLoadedEvent>(OnProfile);
        SubscribeLocalEvent<InteractionPanelPreferencesComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<InteractionPanelPreferencesComponent, ExaminedEvent>(OnExamine);
        SubscribeNetworkEvent<InteractionPanelLibraryRequestEvent>(OnLibraryRequest);
        SubscribeNetworkEvent<InteractionPanelCustomSaveEvent>(OnCustomSave);
        SubscribeNetworkEvent<InteractionPanelCustomDeleteEvent>(OnCustomDelete);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _askLimiter.Clear(); _askHistory.Clear(); _pending.Clear(); _repeats.Clear(); _panels.Clear(); _cooldowns.Clear();
        });
        SubscribeNetworkEvent<InteractionPanelOpenEvent>(OnOpen);
        SubscribeNetworkEvent<InteractionPanelActionEvent>(OnAction);
        SubscribeNetworkEvent<InteractionPanelAnswerEvent>(OnAnswer);
        SubscribeNetworkEvent<InteractionPanelRepeatEvent>(OnRepeat);
        SubscribeNetworkEvent<InteractionPanelCloseEvent>(OnClose);
    }

    private void OnMobStartup(EntityUid uid, MobStateComponent component, ComponentStartup args)
    {
        EnsureComp<InteractionPanelPreferencesComponent>(uid);
        _mana.InitializeMob(uid);
    }

    private void OnProfile(EntityUid uid, HumanoidAppearanceComponent component, ref InteractionPanelProfileLoadedEvent args) =>
        EnsureComp<InteractionPanelPreferencesComponent>(uid).Profile = args.Profile.Clone();

    // Отладочный override не меняет БД. Символ определяется только для Configuration != Release.
    private InteractionPanelConsent Consent(EntityUid uid, InteractionPanelCategory category) => category == InteractionPanelCategory.Neutral || InteractionPanelActions.DebugConsentOverride
        ? InteractionPanelConsent.Yes
        : TryComp<InteractionPanelPreferencesComponent>(uid, out var preferences)
            ? preferences.Profile.GetInteractionPanelConsent(category) : InteractionPanelConsent.Ask;

    private bool CanReach(EntityUid user, EntityUid target) =>
        Exists(user) && Exists(target) &&
        TryComp<MobStateComponent>(user, out var userMob) && userMob.CurrentState == MobState.Alive &&
        TryComp<MobStateComponent>(target, out var targetMob) && targetMob.CurrentState == MobState.Alive &&
        _blocker.CanInteract(user, target) && _interaction.InRangeAndAccessible(user, target);

    private bool HasPlayer(EntityUid target) =>
        TryComp<ActorComponent>(target, out var actor) && actor.PlayerSession.AttachedEntity == target &&
        actor.PlayerSession.Status == Robust.Shared.Enums.SessionStatus.InGame;

    private bool CanUse(EntityUid user, EntityUid target) =>
        CanReach(user, target) && (InteractionPanelActions.DebugConsentOverride || HasPlayer(target));

    private Sex? BodySex(EntityUid uid) => TryComp<HumanoidAppearanceComponent>(uid, out var appearance) ? appearance.Sex : null;

    private bool Allowed(EntityUid user, EntityUid target, InteractionPanelActionDefinition action) =>
        action.Supports(user == target, HasComp<HumanoidAppearanceComponent>(target)) && action.SupportsSexes(BodySex(user), BodySex(target)) && Consent(user, action.Category) != InteractionPanelConsent.No && Consent(target, action.Category) != InteractionPanelConsent.No;

    private void OnVerbs(EntityUid uid, InteractionPanelPreferencesComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !CanReach(args.User, uid)) return;
        args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("dl-interaction-panel-open"), Act = () => OpenPanel(args.User, uid) });
    }

    private void OnOpen(InteractionPanelOpenEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user ||
            !TryGetEntity(args.Target, out var target) || target is not { } entity) return;
        OpenPanel(user, entity);
    }

    private void OpenPanel(EntityUid user, EntityUid target)
    {
        if (!CanReach(user, target) || !TryComp<ActorComponent>(user, out var actor)) return;
        if (!InteractionPanelActions.DebugConsentOverride && !HasPlayer(target))
        {
            _popup.PopupEntity(Loc.GetString("dl-interaction-panel-no-player"), target, user);
            return;
        }
        Stop(user);
        _panels[user] = new Panel(target, actor.PlayerSession, _timing.CurTime + TimeSpan.FromMinutes(10));
        var actions = InteractionPanelActionCatalog.Load(_prototypes).Where(a => Allowed(user, target, a)).Select(a => a.Id).ToArray();
        RaiseNetworkEvent(new InteractionPanelPanelEvent(GetNetEntity(target), Name(target), actions, user == target, InteractionPanelActions.DebugConsentOverride, HasComp<HumanoidAppearanceComponent>(target), BodySex(user), BodySex(target)), actor.PlayerSession);
    }

    private void OnExamine(EntityUid uid, InteractionPanelPreferencesComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange) return;
        foreach (var category in Enum.GetValues<InteractionPanelCategory>().Where(c => c != InteractionPanelCategory.Neutral))
            args.PushText(Loc.GetString("dl-interaction-panel-examine",
                ("category", Loc.GetString($"dl-interaction-panel-category-{category.ToString().ToLowerInvariant()}")),
                ("status", Consent(uid, category).ToString())));
    }

    private bool ValidPanel(EntityUid user, ICommonSession session, NetEntity target) =>
        _panels.TryGetValue(user, out var panel) && panel.Session == session && session.AttachedEntity == user &&
        panel.Expires > _timing.CurTime && CanUse(user, panel.Target) && GetNetEntity(panel.Target) == target;

    private void OnAction(InteractionPanelActionEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user) return;
        if (!ValidPanel(user, session.SenderSession, args.Target)) { Stop(user); Status(user, "dl-interaction-panel-unavailable"); return; }
        var action = FindAction(user, args.Action);
        if (action == null || !Allowed(user, _panels[user].Target, action)) { Status(user, "dl-interaction-panel-unavailable"); return; }
        Stop(user, action.Category);
        Request(user, args.Action);
    }

    private void OnRepeat(InteractionPanelRepeatEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user) return;
        if (!ValidPanel(user, session.SenderSession, args.Target)) { Stop(user); Status(user, "dl-interaction-panel-unavailable"); return; }
        if (!args.Enabled && args.Action == "") { Stop(user); Status(user, "dl-interaction-panel-stopped"); return; }
        var action = FindAction(user, args.Action);
        if (action == null || !Allowed(user, _panels[user].Target, action)) { Status(user, "dl-interaction-panel-unavailable"); return; }
        var key = (user, action.Category);
        if (!args.Enabled)
        {
            if (_repeats.TryGetValue(key, out var active) && active.Action == action.Id) Stop(user, action.Category);
            Status(user, "dl-interaction-panel-stopped");
            return;
        }
        var interval = InteractionPanelActions.ClampInterval(args.Interval);
        if (_repeats.TryGetValue(key, out var current) && current.Action == args.Action)
        {
            current.Interval = interval;
            current.Next = _timing.CurTime + TimeSpan.FromSeconds(interval);
            Status(user, "dl-interaction-panel-repeating");
            return;
        }
        Stop(user, action.Category);
        _repeats[key] = new Repeat(args.Action, interval, _timing.CurTime + TimeSpan.FromSeconds(interval));
        Status(user, "dl-interaction-panel-repeating");
        Request(user, args.Action);
    }

    private void OnClose(InteractionPanelCloseEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user || !_panels.TryGetValue(user, out var panel) ||
            panel.Session != session.SenderSession || !Exists(panel.Target) || GetNetEntity(panel.Target) != args.Target) return;
        Stop(user);
        _panels.Remove(user);
    }

    private void Request(EntityUid user, string actionId)
    {
        if (!_panels.TryGetValue(user, out var panel)) return;
        var action = FindAction(user, actionId);
        if (!CanUse(user, panel.Target))
        {
            Stop(user); Status(user, "dl-interaction-panel-unavailable"); return;
        }
        if (action == null) return;
        if (!Allowed(user, panel.Target, action))
        {
            Stop(user, action.Category); Status(user, "dl-interaction-panel-unavailable"); return;
        }
        if (_cooldowns.TryGetValue((user, action.Category), out var until) && until > _timing.CurTime)
        {
            Status(user, "dl-interaction-panel-cooldown"); return;
        }
        _cooldowns[(user, action.Category)] = _timing.CurTime + TimeSpan.FromSeconds(InteractionPanelActions.MinInterval);
        _panels[user] = panel with { Expires = _timing.CurTime + TimeSpan.FromMinutes(10) };
        // На себе нажатие является согласием. Ask другого игрока разрешает только один запуск.
        if (user != panel.Target && Consent(panel.Target, action.Category) == InteractionPanelConsent.Ask)
        {
            if (!TryComp<ActorComponent>(panel.Target, out var recipient)) { Stop(user, action.Category); Status(user, "dl-interaction-panel-unavailable"); return; }
            if (_pending.Values.Any(p => p.Target == panel.Target) ||
                !_askLimiter.TryRequest(panel.Session.UserId.UserId, recipient.PlayerSession.UserId.UserId, _timing.CurTime))
            {
                Stop(user, action.Category); Status(user, "dl-interaction-panel-ask-limited"); return;
            }
            // Ask разрешает один запуск. Автоповтор не создаёт новые окна запросов.
            _repeats.Remove((user, action.Category));
            var pending = new Pending(Guid.NewGuid(), user, panel.Target, action.Category, panel.Session, recipient.PlayerSession,
                action.Id, _timing.CurTime + TimeSpan.FromSeconds(20));
            _pending[pending.Token] = pending;
            _askHistory[pending.Token] = (panel.Session.UserId.UserId, recipient.PlayerSession.UserId.UserId, _timing.CurTime + TimeSpan.FromMinutes(2));
            RaiseNetworkEvent(new InteractionPanelAskEvent(pending.Token, Name(user), action.Id, action.Title, action.Template == null ? null : InteractionPanelCustomRules.Render(action.Template, Name(user), Name(panel.Target))), recipient.PlayerSession);
            Status(user, "dl-interaction-panel-sent");
            return;
        }
        Execute(user, panel.Target, action);
    }

    private void OnAnswer(InteractionPanelAnswerEvent args, EntitySessionEventArgs session)
    {
        // Получатель может заблокировать отправителя даже после закрытия им панели.
        if (!args.Accepted && args.Block && _askHistory.TryGetValue(args.Token, out var issued) &&
            issued.Recipient == session.SenderSession.UserId.UserId && issued.Expires > _timing.CurTime)
            _askLimiter.Decline(issued.Sender, issued.Recipient, _timing.CurTime, true);
        if (session.SenderSession.AttachedEntity is not { } target || !_pending.TryGetValue(args.Token, out var pending) ||
            pending.Target != target || pending.Recipient != session.SenderSession) return;
        _pending.Remove(args.Token);
        if (!args.Accepted) { _askLimiter.Decline(pending.Session.UserId.UserId, pending.Recipient.UserId.UserId, _timing.CurTime, args.Block); Stop(pending.User, pending.Category); Status(pending.User, "dl-interaction-panel-declined"); return; }
        var action = FindAction(pending.User, pending.Action);
        if (pending.Expires < _timing.CurTime || !ValidPanel(pending.User, pending.Session, GetNetEntity(target)) ||
            action == null || !Allowed(pending.User, target, action)) { Stop(pending.User, pending.Category); Status(pending.User, "dl-interaction-panel-unavailable"); return; }
        Execute(pending.User, target, action);
    }

    private void Execute(EntityUid user, EntityUid target, InteractionPanelActionDefinition action)
    {
        // Все эффекты запускаются здесь, после серверных проверок; клиент не выбирает пути звуков/прототипы.
        var key = user == target ? $"dl-interaction-panel-self-{action.Id}" : $"dl-interaction-panel-result-{action.Id}";
        var message = action.Template == null ? Loc.GetString(key, ("user", Name(user)), ("target", Name(target)))
            : InteractionPanelCustomRules.Render(action.Template, Name(user), Name(target));
        _adminLog.Add(LogType.Chat, LogImpact.Low,
            $"InteractionPanel interaction {action.Id} [{action.Category}] from {ToPrettyString(user):Player} to {ToPrettyString(target):Player}: {message}; sound={action.Sound}");

        // Только чат участников, без PopupEntity (он дублирует сообщение в чат).
        // Invalid исключает облачко emote над мобом. Имена экранируются от markup.
        var wrapped = FormattedMessage.EscapeText(message);
        var color = Color.FromHex(action.ChatColor);
        foreach (var participant in new[] { user, target }.Distinct())
            if (TryComp<ActorComponent>(participant, out var actor))
                _chat.ChatMessageToOne(ChatChannel.Emotes, message, wrapped, EntityUid.Invalid, false, actor.PlayerSession.Channel, color);
        _mana.PlayActionSound(target, action);
        _mana.AddMana(user, target, action.ManaGain);
        if (action.Effect != null) Spawn(action.Effect, new EntityCoordinates(target, Vector2.Zero));
        if (action.Animation != null) RaiseNetworkEvent(new InteractionPanelAnimationEvent(GetNetEntity(user), GetNetEntity(target), action.Animation.Clamped()), Filter.Pvs(user));
        if (_repeats.TryGetValue((user, action.Category), out var repeat)) repeat.Next = _timing.CurTime + TimeSpan.FromSeconds(repeat.Interval);
        Status(user, _repeats.Keys.Any(k => k.User == user) ? "dl-interaction-panel-repeating" : "dl-interaction-panel-done");
    }

    // Без категории останавливаем всё (закрытие панели, потеря цели, общая кнопка Стоп).
    // С категорией затрагиваем только её повтор и запрос согласия.
    private void Stop(EntityUid user, InteractionPanelCategory? category = null)
    {
        foreach (var key in _repeats.Keys.Where(k => k.User == user && (category == null || k.Category == category)).ToArray())
            _repeats.Remove(key);
        foreach (var (token, pending) in _pending.ToArray())
            if (pending.User == user && (category == null || pending.Category == category)) _pending.Remove(token);
    }

    private void Status(EntityUid user, string key)
    {
        if (!TryComp<ActorComponent>(user, out var actor)) return;
        var actions = _repeats.Where(p => p.Key.User == user).OrderBy(p => p.Key.Category).Select(p => p.Value.Action).ToArray();
        RaiseNetworkEvent(new InteractionPanelStatusEvent(key, actions), actor.PlayerSession);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_nextUpdate > _timing.CurTime) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(0.2);
        foreach (var (token, issued) in _askHistory.ToArray())
            if (issued.Expires <= _timing.CurTime) _askHistory.Remove(token);
        foreach (var (token, pending) in _pending.ToArray())
        {
            if (pending.Expires > _timing.CurTime && Exists(pending.Target) && Exists(pending.User) &&
                pending.Session.AttachedEntity == pending.User && pending.Recipient.AttachedEntity == pending.Target) continue;
            _pending.Remove(token);
            _askLimiter.Decline(pending.Session.UserId.UserId, pending.Recipient.UserId.UserId, _timing.CurTime);
            Stop(pending.User, pending.Category);
            Status(pending.User, "dl-interaction-panel-expired");
        }
        foreach (var (user, panel) in _panels.ToArray())
        {
            if (panel.Expires > _timing.CurTime && panel.Session.AttachedEntity == user && CanUse(user, panel.Target)) continue;
            Stop(user); _panels.Remove(user); Status(user, "dl-interaction-panel-unavailable");
        }
        foreach (var (key, repeat) in _repeats.ToArray())
        {
            if (repeat.Next > _timing.CurTime || _pending.Values.Any(p => p.User == key.User && p.Category == key.Category)) continue;
            repeat.Next = _timing.CurTime + TimeSpan.FromSeconds(repeat.Interval);
            Request(key.User, repeat.Action);
        }
        foreach (var (key, until) in _cooldowns.ToArray())
            if (until < _timing.CurTime || !Exists(key.User)) _cooldowns.Remove(key);
    }
}

