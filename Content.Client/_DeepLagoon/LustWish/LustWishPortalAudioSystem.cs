using System.Numerics;
using Content.Shared._DeepLagoon.LustWish;
using Robust.Client.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Map;

namespace Content.Client._DeepLagoon.LustWish;

public sealed partial class LustWishPortalAudioSystem : EntitySystem
{
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    private readonly HashSet<EntityUid> _modified = new();

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(AudioSystem));
        UpdatesBefore.Add(typeof(LustWishVisualSystem));
    }

    public override void FrameUpdate(float frameTime)
    {
        var listener = _audio.GetListenerCoordinates();
        var routes = new List<(MapCoordinates Near, MapCoordinates Far)>();
        var portals = EntityQueryEnumerator<LustWishPortalOccupantComponent>();
        while (portals.MoveNext(out _, out var occupant))
        {
            if (!TryComp<TransformComponent>(occupant.Entrance, out _) || !TryComp<TransformComponent>(occupant.Exit, out _)) continue;
            var a = _transforms.GetMapCoordinates(occupant.Entrance); var b = _transforms.GetMapCoordinates(occupant.Exit);
            if (listener.MapId == a.MapId) routes.Add((a,b));
            if (listener.MapId == b.MapId) routes.Add((b,a));
        }
        var updated = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<AudioComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sound, out var transform))
        {
            if (sound.Global) continue;
            var origin = _transforms.GetMapCoordinates(uid);
            var direct = listener.MapId == origin.MapId ? Vector2.Distance(origin.Position, listener.Position) : float.PositiveInfinity;
            var best = direct; (MapCoordinates Near, MapCoordinates Far)? route = null;
            foreach (var candidate in routes)
            {
                if (candidate.Far.MapId != origin.MapId) continue;
                var distance = Vector2.Distance(listener.Position,candidate.Near.Position) + Vector2.Distance(origin.Position,candidate.Far.Position);
                if (distance >= best || distance > sound.MaxDistance) continue;
                best = distance; route = candidate;
            }
            if (route is not { } r)
            {
                if (_modified.Contains(uid))
                {
                    sound.Position = origin.Position;
                    if (direct > sound.MaxDistance) sound.Gain = 0;
                    else sound.Volume = sound.Params.Volume;
                }
                continue;
            }
            var nearDelta = r.Near.Position-listener.Position; var farDelta = origin.Position-r.Far.Position;
            var direction = nearDelta.LengthSquared() > .001f ? Vector2.Normalize(nearDelta) : farDelta.LengthSquared() > .001f ? Vector2.Normalize(farDelta) : Vector2.Zero;
            sound.Position = listener.Position + direction * best;
            sound.Volume = sound.Params.Volume;
            sound.Occlusion = _audio.GetOcclusion(listener,nearDelta,nearDelta.Length()) + _audio.GetOcclusion(r.Far,farDelta,farDelta.Length(),transform.ParentUid);
            sound.Velocity = Vector2.Zero; updated.Add(uid);
        }
        _modified.Clear(); _modified.UnionWith(updated);
    }
}
