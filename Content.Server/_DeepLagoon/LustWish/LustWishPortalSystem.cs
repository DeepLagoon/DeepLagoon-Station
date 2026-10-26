using System.Linq;
using System.Numerics;
using Content.Server.Popups;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.ActionBlocker;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Destructible;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishPortalSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private InteractionPanelSystem _panels = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishPortalKitComponent, AfterInteractEvent>(OnKit);
        SubscribeLocalEvent<LustWishPortalKitComponent, ComponentShutdown>(OnKitShutdown);
        SubscribeLocalEvent<LustWishPortalComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<LustWishProjectionComponent, GetVerbsEvent<AlternativeVerb>>(OnProjectionVerbs);
        SubscribeLocalEvent<LustWishPortalComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<LustWishPortalComponent, StrapAttemptEvent>(OnBuckleAttempt);
        SubscribeLocalEvent<LustWishPortalComponent, StrappedEvent>(OnStrapped);
        SubscribeLocalEvent<LustWishPortalComponent, UnstrappedEvent>((Entity<LustWishPortalComponent> ent, ref UnstrappedEvent ev) => Disconnect(ev.Buckle.Owner));
        SubscribeLocalEvent<LustWishPortalSupportComponent, AnchorStateChangedEvent>((Entity<LustWishPortalSupportComponent> ent, ref AnchorStateChangedEvent ev) => { if (!ev.Anchored) RemoveSupport(ent); });
        SubscribeLocalEvent<LustWishPortalSupportComponent, EntityTerminatingEvent>((Entity<LustWishPortalSupportComponent> ent, ref EntityTerminatingEvent ev) => RemoveSupport(ent));
        SubscribeLocalEvent<LustWishPortalSupportComponent, BreakageEventArgs>((uid, comp, _) => RemoveSupport((uid, comp)));
        SubscribeLocalEvent<LustWishPortalSupportComponent, DestructionEventArgs>((uid, comp, _) => RemoveSupport((uid, comp)));
        InitializeRelay();
    }

    private bool ValidSupport(EntityUid portal, LustWishPortalComponent component) =>
        !TerminatingOrDeleted(portal) && component.Support is { } wall && !TerminatingOrDeleted(wall) &&
        TryComp<TransformComponent>(wall, out var support) && support.Anchored && support.GridUid != null && Transform(portal).ParentUid == wall;

    private bool Paired(EntityUid portal, LustWishPortalComponent component) => ValidSupport(portal, component) &&
        component.Peer is { } peer && TryComp<LustWishPortalComponent>(peer, out var other) && other.Peer == portal && ValidSupport(peer, other);

    private void RemoveSupport(Entity<LustWishPortalSupportComponent> ent)
    {
        foreach (var portal in ent.Comp.Portals.ToArray())
        {
            if (!TryComp<LustWishPortalComponent>(portal, out var endpoint)) continue;
            if (endpoint.Participant is { } body) Disconnect(body);
            QueueDel(portal);
        }
        ent.Comp.Portals.Clear();
    }

    private void OnKitShutdown(EntityUid uid, LustWishPortalKitComponent component, ComponentShutdown args)
    {
        if (!component.Completed && component.FirstPortal is { } first && Exists(first)) QueueDel(first);
        component.FirstPortal = null;
    }

    private void OnKit(EntityUid uid, LustWishPortalKitComponent component, AfterInteractEvent args)
    {
        if (args.Handled || component.Completed || args.Target is not { } wall || !args.CanReach || !_hands.IsHolding(args.User, uid, out _) ||
            !_blocker.CanInteract(args.User, wall) || !_tags.HasTag(wall, "Wall") || !Transform(wall).Anchored || Transform(wall).GridUid == null) return;
        args.Handled = true;
        // Keep the surface offset in the support's local coordinates, including moving/rotating grids.
        var userWorld = _transform.GetWorldPosition(args.User);
        var userLocal = Vector2.Transform(userWorld, _transform.GetInvWorldMatrix(wall));
        var offset = MathF.Abs(userLocal.X) > MathF.Abs(userLocal.Y) ? new Vector2(MathF.Sign(userLocal.X) * .55f, 0) : new Vector2(0, MathF.Sign(userLocal.Y) * .55f);
        if (offset == Vector2.Zero) offset = new Vector2(0, -.55f);
        var support = EnsureComp<LustWishPortalSupportComponent>(wall);
        if (support.Portals.Any(p => Exists(p) && Vector2.Distance(Transform(p).LocalPosition, offset) < .1f)) return;
        var portal = Spawn("DLLustWishPortal", new EntityCoordinates(wall, offset));
        var endpoint = Comp<LustWishPortalComponent>(portal); endpoint.Support = wall;
        support.Portals.Add(portal); Dirty(portal, endpoint);
        if (component.FirstPortal is not { } first || !TryComp<LustWishPortalComponent>(first, out var firstComp) || !ValidSupport(first, firstComp))
        {
            component.FirstPortal = portal;
            _popup.PopupEntity(Loc.GetString("dl-lustwish-portal-first"), args.User, args.User); return;
        }
        firstComp.Peer = portal; endpoint.Peer = first;
        Dirty(first, firstComp); Dirty(portal, endpoint); component.Completed = true;
        _popup.PopupEntity(Loc.GetString("dl-lustwish-portal-linked"), args.User, args.User); QueueDel(uid);
    }

    private bool CanParticipate(EntityUid portal, EntityUid user) => Exists(user) &&
        TryComp<MobStateComponent>(user, out var mob) && mob.CurrentState == MobState.Alive &&
        _blocker.CanInteract(user, portal) && _interaction.InRangeAndAccessible(user, portal);

    private void OnBuckleAttempt(Entity<LustWishPortalComponent> ent, ref StrapAttemptEvent ev)
    {
        if (!Paired(ent, ent.Comp) || ent.Comp.Participant != null ||
            Comp<LustWishPortalComponent>(ent.Comp.Peer!.Value).Participant != null || !CanParticipate(ent, ev.Buckle.Owner)) ev.Cancelled = true;
    }

    private void OnStrapped(Entity<LustWishPortalComponent> ent, ref StrappedEvent ev)
    {
        if (!Paired(ent, ent.Comp) || ent.Comp.Peer is not { } peer) { _buckle.Unbuckle((ev.Buckle.Owner, (BuckleComponent?)ev.Buckle.Comp), null); return; }
        var body = ev.Buckle.Owner;
        ent.Comp.Participant = body; var other = Comp<LustWishPortalComponent>(peer); other.Participant = body;
        var projection = Spawn("DLLustWishPortalProjection", new EntityCoordinates(peer, Vector2.Zero));
        EntityManager.System<MetaDataSystem>().SetEntityName(projection, Name(body));
        var projected = Comp<LustWishProjectionComponent>(projection); projected.Body = body; projected.Portal = peer; Dirty(projection, projected);
        var occupant = EnsureComp<LustWishPortalOccupantComponent>(body);
        occupant.Entrance = ent; occupant.Exit = peer; occupant.Projection = projection;
        Dirty(body, occupant); Dirty(ent, ent.Comp); Dirty(peer, other);
        UpdateRelayViews();
    }

    public bool TryConnect(EntityUid portal, EntityUid user) => TryComp<LustWishPortalComponent>(portal, out var endpoint) &&
        Paired(portal, endpoint) && CanParticipate(portal, user) && _buckle.TryBuckle(user, user, portal);

    public void Disconnect(EntityUid user)
    {
        if (!TryComp<LustWishPortalOccupantComponent>(user, out var occupant)) return;
        var entrance = occupant.Entrance; var exit = occupant.Exit; var projection = occupant.Projection;
        var safeCoordinates = _transform.GetMapCoordinates(user);
        var safeGrid = Transform(user).GridUid;
        RemComp<LustWishPortalOccupantComponent>(user);
        foreach (var portal in new[] { entrance, exit })
            if (TryComp<LustWishPortalComponent>(portal, out var endpoint) && endpoint.Participant == user)
            { endpoint.Participant = null; Dirty(portal, endpoint); }
        if (Exists(projection)) QueueDel(projection);
        if (TerminatingOrDeleted(user) || Transform(user).MapUid is { } map && TerminatingOrDeleted(map))
        { UpdateRelayViews(); return; }
        // Leave the support hierarchy first: normal unbuckling would otherwise
        // place the body beside its portal under a wall that is already terminating.
        _transform.SetMapCoordinates(user, safeCoordinates);
        if (TryComp<BuckleComponent>(user, out var buckle) && buckle.BuckledTo == entrance) _buckle.Unbuckle((user, buckle), null);
        if (safeGrid is { } grid && !TerminatingOrDeleted(grid)) _transform.AttachToGridOrMap(user);
        UpdateRelayViews();
    }

    public bool CanRelay(EntityUid user, EntityUid target)
    {
        if (!TryComp<MobStateComponent>(target, out var targetMob) || targetMob.CurrentState != MobState.Alive ||
            !TryComp<LustWishPortalOccupantComponent>(target, out var occupant) || !TryComp<BuckleComponent>(target, out var buckle) ||
            buckle.BuckledTo != occupant.Entrance || !TryComp<LustWishPortalComponent>(occupant.Entrance, out var entrance) ||
            entrance.Participant != target || !Paired(occupant.Entrance, entrance)) return false;
        return CanParticipate(occupant.Entrance, user) || CanParticipate(occupant.Exit, user);
    }

    public EntityUid ResolveTarget(EntityUid user, EntityUid target) => TryComp<LustWishProjectionComponent>(target, out var projection) &&
        CanRelay(user, projection.Body) ? projection.Body : target;

    private void OnProjectionVerbs(Entity<LustWishProjectionComponent> ent, ref GetVerbsEvent<AlternativeVerb> ev)
    {
        if (!CanRelay(ev.User, ent.Comp.Body)) return;
        var user = ev.User; var body = ent.Comp.Body;
        ev.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-lustwish-portal-interact"), Act = () => _panels.OpenPanel(user, body) });
    }

    private void OnVerbs(EntityUid uid, LustWishPortalComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!Paired(uid, component) || !CanParticipate(uid, args.User)) return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(HasComp<LustWishPortalOccupantComponent>(user) ? "dl-lustwish-portal-disconnect" : "dl-lustwish-portal-connect"),
            Act = () => { if (HasComp<LustWishPortalOccupantComponent>(user)) Disconnect(user); else TryConnect(uid, user); },
        });
        if (component.Participant is not { } target || !CanRelay(user, target)) return;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-lustwish-portal-interact"), Act = () => _panels.OpenPanel(user, target) });
    }

    private void OnShutdown(EntityUid uid, LustWishPortalComponent component, ComponentShutdown args)
    {
        if (component.Participant is { } body) Disconnect(body);
        if (component.Support is { } wall && TryComp<LustWishPortalSupportComponent>(wall, out var support)) support.Portals.Remove(uid);
        if (component.Peer is { } peer && TryComp<LustWishPortalComponent>(peer, out var other) && other.Peer == uid)
        { other.Peer = null; Dirty(peer, other); }
        component.Peer = null; component.Participant = null;
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<LustWishPortalComponent>();
        while (query.MoveNext(out var uid, out var portal))
        {
            if (!ValidSupport(uid, portal)) { QueueDel(uid); continue; }
            if (portal.Participant is { } body && (!TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive)) Disconnect(body);
        }
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(.25); UpdateRelayViews();
    }
}
