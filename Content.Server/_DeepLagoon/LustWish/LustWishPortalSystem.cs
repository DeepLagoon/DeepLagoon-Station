using Content.Server.Popups;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishPortalSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private InteractionPanelSystem _panels = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishPortalKitComponent, UseInHandEvent>(OnKit);
        SubscribeLocalEvent<LustWishPortalKitComponent, ComponentShutdown>(OnKitShutdown);
        SubscribeLocalEvent<LustWishPortalComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<LustWishPortalComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnKitShutdown(EntityUid uid, LustWishPortalKitComponent component, ComponentShutdown args)
    {
        if (!component.Completed && component.FirstPortal is { } first && Exists(first)) QueueDel(first);
        component.FirstPortal = null;
    }

    private void OnKit(EntityUid uid, LustWishPortalKitComponent component, UseInHandEvent args)
    {
        if (args.Handled || component.Completed || !_hands.IsHolding(args.User, uid, out _) || !_blocker.CanInteract(args.User, uid)) return;
        args.Handled = true;
        var portal = Spawn("DLLustWishPortal", Transform(args.User).Coordinates);
        if (component.FirstPortal is not { } first || !TryComp<LustWishPortalComponent>(first, out var firstComp))
        {
            component.FirstPortal = portal;
            _popup.PopupEntity(Loc.GetString("dl-lustwish-portal-first"), args.User, args.User);
            return;
        }
        firstComp.Peer = portal;
        Comp<LustWishPortalComponent>(portal).Peer = first;
        component.Completed = true;
        _popup.PopupEntity(Loc.GetString("dl-lustwish-portal-linked"), args.User, args.User);
        QueueDel(uid);
    }

    private bool CanParticipate(EntityUid portal, EntityUid user) =>
        Exists(portal) && Exists(user) && HasComp<ActorComponent>(user) &&
        TryComp<MobStateComponent>(user, out var mob) && mob.CurrentState == MobState.Alive &&
        _blocker.CanInteract(user, portal) && _interaction.InRangeAndAccessible(user, portal);

    public bool TryConnect(EntityUid portal, EntityUid user)
    {
        if (!TryComp<LustWishPortalComponent>(portal, out var component) || !CanParticipate(portal, user) ||
            component.Peer is not { } peer || !TryComp<LustWishPortalComponent>(peer, out var other) || other.Peer != portal ||
            component.Participant is { } occupant && occupant != user && CanParticipate(portal, occupant)) return false;
        Disconnect(user);
        component.Participant = user;
        return true;
    }

    public void Disconnect(EntityUid user)
    {
        var query = EntityQueryEnumerator<LustWishPortalComponent>();
        while (query.MoveNext(out _, out var portal))
            if (portal.Participant == user) portal.Participant = null;
    }

    public bool CanRelay(EntityUid user, EntityUid target)
    {
        if (user == target) return false;
        var query = EntityQueryEnumerator<LustWishPortalComponent>();
        while (query.MoveNext(out var uid, out var portal))
        {
            if (portal.Participant != target || !CanParticipate(uid, target) || portal.Peer is not { } peer ||
                !TryComp<LustWishPortalComponent>(peer, out var other) || other.Peer != uid || !CanParticipate(peer, user)) continue;
            return true;
        }
        return false;
    }

    private EntityUid? Target(EntityUid portal, EntityUid user)
    {
        if (!TryComp<LustWishPortalComponent>(portal, out var endpoint) || endpoint.Peer is not { } peer ||
            !TryComp<LustWishPortalComponent>(peer, out var other) || other.Participant is not { } target ||
            !CanParticipate(portal, user) || !CanRelay(user, target)) return null;
        return target;
    }

    private void OnVerbs(EntityUid uid, LustWishPortalComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !CanParticipate(uid, args.User)) return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(component.Participant == args.User ? "dl-lustwish-portal-disconnect" : "dl-lustwish-portal-connect"),
            Act = () =>
            {
                if (!CanParticipate(uid, args.User)) return;
                if (component.Participant == args.User) Disconnect(args.User);
                else if (!TryConnect(uid, args.User)) _popup.PopupEntity(Loc.GetString("dl-lustwish-portal-unavailable"), uid, args.User);
            },
        });
        if (Target(uid, args.User) is not { } target) return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("dl-lustwish-portal-interact"),
            Act = () => { if (Target(uid, args.User) == target) _panels.OpenPanel(args.User, target); },
        });
    }

    private void OnShutdown(EntityUid uid, LustWishPortalComponent component, ComponentShutdown args)
    {
        if (component.Peer is { } peer && TryComp<LustWishPortalComponent>(peer, out var other) && other.Peer == uid)
        {
            other.Peer = null;
            other.Participant = null;
        }
        component.Peer = null;
        component.Participant = null;
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(0.25);
        var query = EntityQueryEnumerator<LustWishPortalComponent>();
        while (query.MoveNext(out var uid, out var portal))
            if (portal.Participant is { } participant && !CanParticipate(uid, participant)) portal.Participant = null;
    }
}
