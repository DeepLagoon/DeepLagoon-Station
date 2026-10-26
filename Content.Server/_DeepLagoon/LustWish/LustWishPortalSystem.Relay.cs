using System.Collections.Generic;
using Content.Server.Chat.Systems;
using Content.Shared._DeepLagoon.LustWish;
using Robust.Server.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishPortalSystem
{
    [Dependency] private ViewSubscriberSystem _views = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    private readonly HashSet<(EntityUid Portal, ICommonSession Session)> _relayViews = new();

    private void InitializeRelay() => SubscribeLocalEvent<ExpandICChatRecipientsEvent>(OnRecipients);

    private void OnRecipients(ExpandICChatRecipientsEvent ev)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } listener) continue;
            var distance = AcousticDistance(ev.Source, listener);
            if (distance > ev.VoiceRange) continue;
            if (!ev.Recipients.TryGetValue(session, out var old) || old.Range >= 0 && distance < old.Range)
                ev.Recipients[session] = new ChatSystem.ICChatRecipientData(distance, false);
        }
    }

    public float AcousticDistance(EntityUid source, EntityUid listener)
    {
        if (!Exists(source) || !Exists(listener)) return float.PositiveInfinity;
        var from = Transform(source).Coordinates; var to = Transform(listener).Coordinates;
        var result = from.TryDistance(EntityManager, to, out var direct) ? direct : float.PositiveInfinity;
        var query = EntityQueryEnumerator<LustWishPortalOccupantComponent>();
        while (query.MoveNext(out var body, out var c))
        {
            if (!TryComp<LustWishPortalComponent>(c.Entrance, out var portal) || !Paired(c.Entrance, portal) ||
                !TryComp<Content.Shared.Buckle.Components.BuckleComponent>(body, out var buckle) || buckle.BuckledTo != c.Entrance) continue;
            var a = Transform(c.Entrance).Coordinates; var b = Transform(c.Exit).Coordinates;
            if (from.TryDistance(EntityManager,a,out var fa) && to.TryDistance(EntityManager,b,out var tb)) result = MathF.Min(result,fa+tb);
            if (from.TryDistance(EntityManager,b,out var fb) && to.TryDistance(EntityManager,a,out var ta)) result = MathF.Min(result,fb+ta);
        }
        return result;
    }

    private void UpdateRelayViews()
    {
        var wanted = new HashSet<(EntityUid Portal, ICommonSession Session)>();
        var query = EntityQueryEnumerator<LustWishPortalOccupantComponent>();
        while (query.MoveNext(out var body, out var c))
        {
            if (!TryComp<LustWishPortalComponent>(c.Entrance, out var portal) || !Paired(c.Entrance, portal)) continue;
            foreach (var session in _players.Sessions)
            {
                if (session.AttachedEntity is not { } listener || !Exists(listener)) continue;
                var coords = Transform(listener).Coordinates;
                if (coords.TryDistance(EntityManager,Transform(c.Entrance).Coordinates,out var a) && a <= 16) wanted.Add((c.Exit,session));
                if (coords.TryDistance(EntityManager,Transform(c.Exit).Coordinates,out var b) && b <= 16) wanted.Add((c.Entrance,session));
            }
        }
        foreach (var pair in _relayViews)
            if (!wanted.Contains(pair) && Exists(pair.Portal)) _views.RemoveViewSubscriber(pair.Portal,pair.Session);
        foreach (var pair in wanted)
            if (!_relayViews.Contains(pair)) _views.AddViewSubscriber(pair.Portal,pair.Session);
        _relayViews.Clear(); _relayViews.UnionWith(wanted);
    }

    public override void Shutdown()
    {
        foreach (var pair in _relayViews) if (Exists(pair.Portal)) _views.RemoveViewSubscriber(pair.Portal,pair.Session);
        _relayViews.Clear(); base.Shutdown();
    }
}
