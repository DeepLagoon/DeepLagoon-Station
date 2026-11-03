using Robust.Client.GameObjects;
using System.Numerics;
using Content.Client._Emberfall.Weapons.Ranged.Overlays;
using Content.Shared._Emberfall.Weapons.Ranged;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._Emberfall.Weapons.Ranged.Systems;

public sealed partial class TracerSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IOverlayManager _overlay = default!;
    [Dependency] private TransformSystem _transform = default!;

    private const int MaxHistoryPoints = 256;
    private readonly Dictionary<EntityUid, MapId> _historyMaps = new();

    public override void Initialize()
    {
        base.Initialize();
        _overlay.AddOverlay(new TracerOverlay(this));
        UpdatesAfter.Add(typeof(TransformSystem));

        SubscribeLocalEvent<TracerComponent, ComponentStartup>(OnTracerStart);
        SubscribeLocalEvent<TracerComponent, ComponentShutdown>(OnTracerShutdown);
    }

    private void OnTracerStart(Entity<TracerComponent> ent, ref ComponentStartup args)
    {
        var xform = Transform(ent);
        _historyMaps[ent.Owner] = xform.MapID;

        ent.Comp.Data = new TracerData(
            new List<Vector2>(),
            _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.Lifetime)
        );
    }

    private void OnTracerShutdown(Entity<TracerComponent> ent, ref ComponentShutdown args)
    {
        _historyMaps.Remove(ent.Owner);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_timing.IsFirstTimePredicted)
            return;
        var query = EntityQueryEnumerator<TracerComponent>();
        while (query.MoveNext(out var uid, out var tracer))
        {
            if (_timing.CurTime > tracer.Data.EndTime)
                RemCompDeferred<TracerComponent>(uid);
        }
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var query = EntityQueryEnumerator<TracerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var tracer, out var xform))
        {
            var positions = tracer.Data.PositionHistory;
            if (!_historyMaps.TryGetValue(uid, out var map) || map != xform.MapID)
            {
                positions.Clear();
                _historyMaps[uid] = xform.MapID;
            }
            // Sample after interpolation, once per displayed frame. Prediction
            // rollback must not append old tick positions to a visible trail.
            var position = _transform.GetRenderWorldPosition((uid, xform));
            if (positions.Count == 0 || positions[^1] != position)
                positions.Add(position);
            while (positions.Count > 2 &&
                   (positions.Count > MaxHistoryPoints || GetTrailLength(positions) > tracer.Length))
                positions.RemoveAt(0);
        }
    }

    private static float GetTrailLength(List<Vector2> positions)
    {
        var length = 0f;
        for (var i = 1; i < positions.Count; i++)
        {
            length += Vector2.Distance(positions[i - 1], positions[i]);
        }
        return length;
    }

    public void Draw(DrawingHandleWorld handle, MapId currentMap)
    {
        var query = EntityQueryEnumerator<TracerComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var tracer, out var xform))
        {
            if (xform.MapID != currentMap)
                continue;

            var positions = tracer.Data.PositionHistory;

            if (positions.Count < 2)
                continue;

            handle.SetTransform(Matrix3x2.Identity);

            for (var i = 1; i < positions.Count; i++)
            {
                handle.DrawLine(positions[i - 1], positions[i], tracer.Color);
            }
        }
    }
}
