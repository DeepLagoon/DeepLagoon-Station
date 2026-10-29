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
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem : EntitySystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private InteractionPanelArousalSystem _arousal = default!;
    [Dependency] private Content.Server._DeepLagoon.LustWish.LustWishPortalSystem _portals = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;

    private readonly Dictionary<EntityUid, Panel> _panels = new();
    private readonly Dictionary<(EntityUid User, InteractionPanelCategory Category), TimeSpan> _cooldowns = new();
    private readonly Dictionary<(EntityUid User, InteractionPanelCategory Category), Repeat> _repeats = new();
    private TimeSpan _nextUpdate;
    private sealed record Panel(EntityUid Target, ICommonSession Session, TimeSpan Expires);
    private sealed class Repeat(string action, float interval, TimeSpan next)
    {
        public string Action = action;
        public float Interval = interval;
        public TimeSpan Next = next;
    }

    public override void Initialize()
    {
        SubscribeLocalEvent<MobStateComponent, MapInitEvent>(OnMobMapInit);
        SubscribeLocalEvent<HumanoidAppearanceComponent, InteractionPanelProfileLoadedEvent>(OnProfile);
        SubscribeLocalEvent<InteractionPanelPreferencesComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<InteractionPanelPreferencesComponent, ExaminedEvent>(OnExamine);
        SubscribeNetworkEvent<InteractionPanelLibraryRequestEvent>(OnLibraryRequest);
        SubscribeNetworkEvent<InteractionPanelCustomSaveEvent>(OnCustomSave);
        SubscribeNetworkEvent<InteractionPanelCustomDeleteEvent>(OnCustomDelete);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _repeats.Clear(); _panels.Clear(); _cooldowns.Clear(); _nextManualClimax.Clear();
        });
        SubscribeNetworkEvent<InteractionPanelOpenEvent>(OnOpen);
        SubscribeNetworkEvent<InteractionPanelActionEvent>(OnAction);
        SubscribeNetworkEvent<InteractionPanelRepeatEvent>(OnRepeat);
        SubscribeNetworkEvent<InteractionPanelCloseEvent>(OnClose);
        SubscribeNetworkEvent<InteractionPanelBodyVisibilityEvent>(OnBodyVisibility);
        SubscribeNetworkEvent<InteractionPanelClimaxRequestEvent>(OnClimaxRequest);
        SubscribeLocalEvent<InteractionPanelArousalComponent, BlueMoonClimaxDoAfterEvent>(OnClimaxDoAfter);
        SubscribeLocalEvent<InteractionPanelArousalComponent, ComponentShutdown>((uid, _, _) => { _nextManualClimax.Remove(uid); });
        SubscribeLocalEvent<HumanoidAppearanceComponent, Content.Shared._DeepLagoon.BodyCustomization.BodyCustomizationChangedEvent>(OnBodyChanged);
        SubscribeLocalEvent<HumanoidAppearanceComponent, SexChangedEvent>((uid, _, _) => RefreshAvailability(uid));
    }

    private void OnMobMapInit(EntityUid uid, MobStateComponent component, MapInitEvent args)
    {
        EnsureComp<InteractionPanelPreferencesComponent>(uid);
        _arousal.InitializeMob(uid);
    }

    private void OnProfile(EntityUid uid, HumanoidAppearanceComponent component, ref InteractionPanelProfileLoadedEvent args)
    {
        EnsureComp<InteractionPanelPreferencesComponent>(uid).Profile = args.Profile.Clone();
        RefreshAvailability(uid);
        if (_panels.TryGetValue(uid, out var panel)) SendLibrary(panel.Session);
    }

    private InteractionPanelConsent Consent(EntityUid uid, InteractionPanelCategory category) => category == InteractionPanelCategory.Neutral
        ? InteractionPanelConsent.Yes
        : TryComp<InteractionPanelPreferencesComponent>(uid, out var preferences)
            ? preferences.Profile.GetInteractionPanelConsent(category) : InteractionPanelConsent.Ask;

    public bool CanAccessEquipment(EntityUid user, EntityUid target) =>
        _panels.TryGetValue(user, out var panel) && panel.Target == target && panel.Expires > _timing.CurTime &&
        panel.Session.AttachedEntity == user && Exists(user) && Exists(target);

    private bool CanReach(EntityUid user, EntityUid target) =>
        Exists(user) && Exists(target) &&
        TryComp<MobStateComponent>(user, out var userMob) && userMob.CurrentState == MobState.Alive &&
        TryComp<MobStateComponent>(target, out var targetMob) && targetMob.CurrentState == MobState.Alive &&
        _blocker.CanInteract(user, target) && (_interaction.InRangeAndAccessible(user, target) || _portals.CanRelay(user, target));

    private bool HasPlayer(EntityUid target) =>
        TryComp<ActorComponent>(target, out var actor) && actor.PlayerSession.AttachedEntity == target &&
        actor.PlayerSession.Status == Robust.Shared.Enums.SessionStatus.InGame;

    private bool CanUse(EntityUid user, EntityUid target) =>
        CanReach(user, target) && (InteractionPanelActions.DebugConsentOverride || HasPlayer(target));

    private Sex? BodySex(EntityUid uid) => TryComp<HumanoidAppearanceComponent>(uid, out var appearance) ? appearance.Sex : null;

    private bool Allowed(EntityUid user, EntityUid target, InteractionPanelActionDefinition action) =>
        (!action.RequiresRecovered || !TryComp<InteractionPanelArousalComponent>(user, out var arousal) || arousal.RecoveryUntil <= _timing.CurTime) &&
        (action.Category == InteractionPanelCategory.Neutral ||
            Consent(user, InteractionPanelCategory.Erotic) != InteractionPanelConsent.No && Consent(user, action.Category) != InteractionPanelConsent.No) &&
        action.Supports(user == target, HasComp<HumanoidAppearanceComponent>(target)) && action.SupportsSexTraits(BodySexTraits(user), BodySexTraits(target));

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

    public void OpenPanel(EntityUid user, EntityUid target)
    {
        target = _portals.ResolveTarget(user, target);
        if ((!CanReach(user, target) && !(user == target && TryComp<MobStateComponent>(user, out var mob) && mob.CurrentState == MobState.Alive &&
            _blocker.CanConsciouslyPerformAction(user))) || !TryComp<ActorComponent>(user, out var actor)) return;
        if (!InteractionPanelActions.DebugConsentOverride && !HasPlayer(target))
        {
            _popup.PopupEntity(Loc.GetString("dl-interaction-panel-no-player"), target, user);
            return;
        }
        Stop(user);
        _panels[user] = new Panel(target, actor.PlayerSession, _timing.CurTime + TimeSpan.FromMinutes(10));
        var actions = InteractionPanelActionCatalog.Load(_prototypes).Where(a => Allowed(user, target, a)).Select(a => a.Id).ToArray();
        RaiseNetworkEvent(new InteractionPanelPanelEvent(GetNetEntity(target), Name(target), actions, user == target, InteractionPanelActions.DebugConsentOverride, HasComp<HumanoidAppearanceComponent>(target), BodySex(user), BodySex(target), BodySexTraits(user), BodySexTraits(target)), actor.PlayerSession);
        SendBodyState(user);
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
        Execute(user, panel.Target, action);
    }

    private void Execute(EntityUid user, EntityUid target, InteractionPanelActionDefinition action, string? messageKey = null)
    {
        // Все эффекты запускаются здесь, после серверных проверок; клиент не выбирает пути звуков/прототипы.
        var key = messageKey ?? action.MessageKey(user == target, _random);
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
        _arousal.PlayActionSound(target, action);
        _arousal.AdjustArousal(user, target, user == target ? action.UserArousalGain ?? action.TargetArousalGain ?? action.ArousalGain : action.TargetArousalGain ?? action.ArousalGain, interaction: action.RawArousalLimit == null,
            rawLimit: action.RawArousalLimit,
            bodyPart: user == target ? action.UserBodyPart ?? action.TargetBodyPart : action.TargetBodyPart,
            fluidDestination: user == target ? action.UserFluidDestination ?? action.TargetFluidDestination : action.TargetFluidDestination);
        if (user != target)
            _arousal.AdjustArousal(target, user, action.UserArousalGain ?? action.ArousalGain, interaction: action.RawArousalLimit == null, rawLimit: action.RawArousalLimit,
                bodyPart: action.UserBodyPart, fluidDestination: action.UserFluidDestination);
        if (action.Effect != null) Spawn(action.Effect, new EntityCoordinates(target, Vector2.Zero));
        if (action.Animation != null && _interaction.InRangeAndAccessible(user, target)) RaiseNetworkEvent(new InteractionPanelAnimationEvent(GetNetEntity(user), GetNetEntity(target), action.Animation.Clamped()), Filter.Pvs(user));
        if (_repeats.TryGetValue((user, action.Category), out var repeat)) repeat.Next = _timing.CurTime + TimeSpan.FromSeconds(repeat.Interval);
        Status(user, _repeats.Keys.Any(k => k.User == user) ? "dl-interaction-panel-repeating" : "dl-interaction-panel-done");
    }

    // Без категории останавливаем всё (закрытие панели, потеря цели, общая кнопка Стоп).
    // С категорией затрагиваем только её повтор.
    private void Stop(EntityUid user, InteractionPanelCategory? category = null)
    {
        foreach (var key in _repeats.Keys.Where(k => k.User == user && (category == null || k.Category == category)).ToArray())
            _repeats.Remove(key);
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
        foreach (var (user, panel) in _panels.ToArray())
        {
            if (panel.Expires > _timing.CurTime && panel.Session.AttachedEntity == user && CanUse(user, panel.Target)) continue;
            Stop(user); _panels.Remove(user); Status(user, "dl-interaction-panel-unavailable");
        }
        foreach (var (key, repeat) in _repeats.ToArray())
        {
            if (repeat.Next > _timing.CurTime) continue;
            repeat.Next = _timing.CurTime + TimeSpan.FromSeconds(repeat.Interval);
            Request(key.User, repeat.Action);
        }
        foreach (var (key, until) in _cooldowns.ToArray())
            if (until < _timing.CurTime || !Exists(key.User)) _cooldowns.Remove(key);
    }
}
