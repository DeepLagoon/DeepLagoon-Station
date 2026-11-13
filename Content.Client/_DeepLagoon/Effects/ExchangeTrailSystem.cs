using System.Numerics;
using Content.Shared.Exchanger;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Effects;

public sealed partial class ExchangeTrailSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize() => _overlays.AddOverlay(new TrailOverlay(EntityManager, _transform, _timing));
    public override void Shutdown() => _overlays.RemoveOverlay<TrailOverlay>();

    private sealed class TrailOverlay(IEntityManager entities, TransformSystem transform, IGameTiming timing) : Overlay
    {
        public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

        protected override void Draw(in OverlayDrawArgs args)
        {
            var trails = entities.EntityQueryEnumerator<ExchangeTrailComponent, TransformComponent>();
            var transforms = entities.GetEntityQuery<TransformComponent>();
            while (trails.MoveNext(out var uid, out var trail, out var start))
            {
                if (start.MapID != args.MapId || !transforms.TryComp(trail.Target, out var target) || target.MapID != start.MapID)
                    continue;

                var remaining = trail.EndsAt - timing.CurTime;
                if (remaining <= TimeSpan.Zero)
                    continue;

                var a = transform.GetRenderWorldPosition((uid, start));
                var b = transform.GetRenderWorldPosition((trail.Target, target));
                var delta = b - a;
                if (delta.LengthSquared() < 0.001f)
                    continue;

                var alpha = remaining is { } time && trail.Fade > TimeSpan.Zero
                    ? Math.Clamp((float) (time / trail.Fade), 0, 1)
                    : 1;
                var midpoint = (a + b) * 0.5f;
                var length = delta.Length() * 0.5f;
                DrawBeam(args.WorldHandle, midpoint, delta.ToWorldAngle(), length, trail.HalfWidth * 2,
                    trail.Tint.WithAlpha(trail.Tint.A * alpha * 0.25f));
                DrawBeam(args.WorldHandle, midpoint, delta.ToWorldAngle(), length, trail.HalfWidth,
                    trail.Tint.WithAlpha(trail.Tint.A * alpha));
            }
        }

        private static void DrawBeam(DrawingHandleWorld handle, Vector2 center, Angle angle, float length, float width, Color color)
        {
            var rect = new Box2(-width, -length, width, length).Translated(center);
            handle.DrawRect(new Box2Rotated(rect, angle, center), color);
        }
    }
}
