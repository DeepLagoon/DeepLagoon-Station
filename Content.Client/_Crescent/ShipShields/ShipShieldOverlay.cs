using Robust.Client.GameObjects;
using Content.Shared._Crescent.ShipShields;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Crescent.ShipShields;

public sealed class ShipShieldOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> ShaderId = "ShipShieldSurface";
    private readonly FixtureSystem _fixture;
    private readonly TransformSystem _transform;
    private readonly IEntityManager _entManager;
    private readonly ShaderPrototype _shaderPrototype;
    private readonly Dictionary<EntityUid, ShieldSurface> _surfaces = new();
    private readonly List<EntityUid> _stale = new();

    // Draw over parallax but before grid tiles, so the surface is only visible in space.
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    public ShipShieldOverlay(IEntityManager entityManager, IPrototypeManager prototypeManager)
    {
        _entManager = entityManager;
        _fixture = _entManager.System<FixtureSystem>();
        _transform = _entManager.System<TransformSystem>();
        _shaderPrototype = prototypeManager.Index(ShaderId);
        ZIndex = 8;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        var previousTransform = handle.GetTransform();
        var previousShader = handle.GetShader();

        try
        {
            var query = _entManager.AllEntityQueryEnumerator<ShipShieldVisualsComponent, FixturesComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var visuals, out var fixtures, out var xform))
            {
                if (xform.MapID != args.MapId ||
                    _fixture.GetFixtureOrNull(uid, "shield", fixtures)?.Shape is not ChainShape chain ||
                    chain.Count < 3)
                    continue;

                var surface = GetSurface(uid, chain);
                var matrix = _transform.GetRenderWorldMatrix((xform.Owner, xform));
                if (!args.WorldAABB.Intersects(matrix.TransformBox(surface.Mesh.Bounds)))
                    continue;

                handle.SetTransform(matrix);
                handle.UseShader(surface.Shader);
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, Texture.White,
                    surface.Mesh.Vertices, visuals.ShieldColor);
            }
        }
        finally
        {
            handle.SetTransform(previousTransform);
            handle.UseShader(previousShader);
        }
    }

    private ShieldSurface GetSurface(EntityUid uid, ChainShape chain)
    {
        if (_surfaces.TryGetValue(uid, out var surface))
        {
            // Fixture replication replaces the vertex array when the shape changes.
            if (ReferenceEquals(surface.Mesh.SourceVertices, chain.Vertices))
                return surface;

            surface.Shader.Dispose();
        }

        var mesh = new ShipShieldMesh(chain);
        var shader = _shaderPrototype.InstanceUnique();
        shader.SetParameter("shield_size", mesh.Bounds.Size);
        shader.SetParameter("inner_width_ratio", ShipShieldGeometry.InnerWidthRatio);
        shader.SetParameter("inner_height_ratio", ShipShieldGeometry.InnerHeightRatio);
        surface = new ShieldSurface(mesh, shader);
        _surfaces[uid] = surface;
        return surface;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        // Also release shields when no viewport is drawing this overlay.
        foreach (var (uid, surface) in _surfaces)
        {
            if (_entManager.HasComponent<ShipShieldVisualsComponent>(uid) &&
                _entManager.TryGetComponent<FixturesComponent>(uid, out var fixtures) &&
                _fixture.GetFixtureOrNull(uid, "shield", fixtures)?.Shape is ChainShape)
                continue;

            surface.Shader.Dispose();
            _stale.Add(uid);
        }

        foreach (var uid in _stale)
            _surfaces.Remove(uid);
        _stale.Clear();
    }

    protected override void DisposeBehavior()
    {
        foreach (var surface in _surfaces.Values)
            surface.Shader.Dispose();
        _surfaces.Clear();
        base.DisposeBehavior();
    }

    private sealed record ShieldSurface(ShipShieldMesh Mesh, ShaderInstance Shader);
}
