using System.Linq;
using Content.Shared._DeepLagoon.Chat;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.IdentityManagement;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Verbs;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    [Dependency] private IGameTiming _antighostTiming = default!;

    private sealed record AntighostDraft(int Id, EntityUid Sender, EntityUid Recipient, TimeSpan Expires);
    private readonly Dictionary<NetUserId, AntighostDraft> _antighostDrafts = new();
    private int _nextAntighostRequest;

    private void InitializeAntighost()
    {
        SubscribeLocalEvent<ActorComponent, GetVerbsEvent<AlternativeVerb>>(OnAntighostVerb);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnAntighostPlayerDetached);
        SubscribeNetworkEvent<AntighostEmoteSendEvent>(OnAntighostSend);
    }

    private void OnAntighostPlayerDetached(PlayerDetachedEvent args)
    {
        _antighostDrafts.Remove(args.Player.UserId);
    }

    private void OnAntighostVerb(Entity<ActorComponent> recipient, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var sender = args.User;
        if (sender == recipient.Owner || !CanReceiveAntighost(sender) || !CanReceiveAntighost(recipient) ||
            !InAntighostRange(sender, recipient) || !_actionBlocker.CanEmote(sender))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("dl-antighost-title"),
            Act = () => OpenAntighost(sender, recipient),
        });
    }

    private void OpenAntighost(EntityUid sender, EntityUid recipient)
    {
        if (!TryComp<ActorComponent>(sender, out var actor) || !CanReceiveAntighost(sender) ||
            !CanReceiveAntighost(recipient) || !InAntighostRange(sender, recipient))
            return;

        var request = new AntighostDraft(++_nextAntighostRequest, sender, recipient,
            _antighostTiming.RealTime + TimeSpan.FromMinutes(2));
        _antighostDrafts[actor.PlayerSession.UserId] = request;
        RaiseNetworkEvent(new AntighostEmotePromptEvent(request.Id, Name(Identity.Entity(recipient, EntityManager))),
            actor.PlayerSession);
    }

    private void OnAntighostSend(AntighostEmoteSendEvent message, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_antighostDrafts.TryGetValue(session.UserId, out var draft) || draft.Id != message.RequestId)
            return;

        _antighostDrafts.Remove(session.UserId);
        if (draft.Expires < _antighostTiming.RealTime || session.AttachedEntity != draft.Sender ||
            !CanReceiveAntighost(draft.Sender) || !CanReceiveAntighost(draft.Recipient) ||
            !InAntighostRange(draft.Sender, draft.Recipient) || !TryComp<ActorComponent>(draft.Recipient, out var target))
        {
            _chatManager.ChatMessageToOne(ChatChannel.Notifications, "",
                Loc.GetString("dl-antighost-unavailable"), EntityUid.Invalid, false, session.Channel);
            return;
        }

        var text = message.Text?.Trim() ?? "";
        if (text.Length is 0 or > AntighostEmote.MaxLength || !_actionBlocker.CanEmote(draft.Sender) ||
            _chatManager.HandleRateLimit(session) != RateLimitStatus.Allowed || !CanSendInGame(text, player: session))
            return;

        var identity = Identity.Entity(draft.Sender, EntityManager);
        var wrapped = Loc.GetString("dl-antighost-message",
            ("name", FormattedMessage.EscapeText(Name(identity))),
            ("message", FormattedMessage.EscapeText(text)));
        var recipients = new List<ICommonSession> { target.PlayerSession };
        if (message.IncludeNearby)
        {
            foreach (var listener in _playerManager.Sessions)
            {
                if (listener.AttachedEntity is not { } entity || entity == draft.Sender || entity == draft.Recipient ||
                    !CanReceiveAntighost(entity) || !InAntighostRange(draft.Sender, entity))
                    continue;

                recipients.Add(listener);
            }
        }

        foreach (var listener in recipients)
            _chatManager.ChatMessageToOne(ChatChannel.Emotes, text, wrapped, EntityUid.Invalid, false, listener.Channel,
                recordReplay: false, author: session.UserId);

        var recipientNames = string.Join(", ", recipients.Select(player => ToPrettyString(player.AttachedEntity).ToString()));
        _adminLogger.Add(LogType.Chat, LogImpact.Low,
            $"Emote Antighost from {ToPrettyString(draft.Sender):user} to {recipientNames}: {text}");

        _chatManager.ChatMessageToOne(ChatChannel.Notifications, "",
            Loc.GetString("dl-antighost-sent", ("count", recipients.Count)), EntityUid.Invalid, false, session.Channel);
    }

    private bool CanReceiveAntighost(EntityUid entity) =>
        HasComp<ActorComponent>(entity) && !HasComp<GhostComponent>(entity) &&
        !HasComp<GhostHearingComponent>(entity) && !_mobStateSystem.IsDead(entity);

    private bool InAntighostRange(EntityUid sender, EntityUid recipient) =>
        TryComp<TransformComponent>(sender, out var from) && TryComp<TransformComponent>(recipient, out var to) &&
        from.MapUid != null && from.MapUid == to.MapUid &&
        from.Coordinates.TryDistance(EntityManager, to.Coordinates, out var distance) && distance <= AntighostEmote.Range;
}
