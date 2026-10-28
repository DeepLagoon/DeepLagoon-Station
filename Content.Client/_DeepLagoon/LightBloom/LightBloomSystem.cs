using System.Numerics;
using Content.Client.Light.Components;
using Content.Client.Light.Visualizers;
using Content.Client._Scp.Shaders.Bloom;
using Content.Shared.CCVar;
using Content.Shared.Light.Components;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.ComponentTrees;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics;

namespace Content.Client._DeepLagoon.LightBloom;

public sealed partial class LightBloomSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private LightTreeSystem _lights = default!;
    [Dependency] private ClientOccluderSystem _occluders = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;

    private static readonly ProtoId<ShaderPrototype> BloomShader = "LightBloom";
    private static readonly ProtoId<ShaderPrototype> HiddenShader = "LightSourceHidden";
    private readonly LightBloomGeometry _geometry = new();
    private readonly HashSet<Entity<OccluderComponent, TransformComponent>> _blockers = new();
    private readonly HashSet<EntityUid> _entities = new();
    private readonly List<Entity<SharedPointLightComponent, TransformComponent>> _candidates = new();
    private readonly List<(EntityUid Uid, PointLightComponent Light, Vector2 Position, Angle Rotation, float Distance)> _visible = new();
    private readonly Dictionary<(EntityUid Uid, int Index), (SpriteComponent Sprite, int Index, SpriteComponent.Layer Layer, ShaderInstance? Shader, string? Prototype)> _hidden = new();
    private readonly Vector2[] _positions = new Vector2[LightBloomGeometry.MaxVertices];
    private readonly DrawVertexUV2D[] _vertices = new DrawVertexUV2D[LightBloomGeometry.MaxVertices];
    private BloomOverlay _bloom = default!;
    private VisibilityOverlay _visibility = default!;
    private ShaderInstance _shader = default!;
    private ShaderInstance _hiddenShader = default!;

    public override void Initialize()
    {
        base.Initialize();
        _shader = _prototypes.Index(BloomShader).InstanceUnique();
        _hiddenShader = _prototypes.Index(HiddenShader).Instance();
        _bloom = new BloomOverlay(this);
        _visibility = new VisibilityOverlay(this);
        _overlays.AddOverlay(_visibility);
        _overlays.AddOverlay(_bloom);
        SubscribeLocalEvent<SpriteComponent, ComponentShutdown>((uid, _, _) => Restore(uid));
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay(_visibility);
        _overlays.RemoveOverlay(_bloom);
        _visibility.Dispose();
        _bloom.Dispose();
        foreach (var entry in _hidden.Values)
            RestoreLayer(entry);
        _hidden.Clear();
        _shader.Dispose();
        base.Shutdown();
    }

    private void CollectBlockers(in OverlayDrawArgs args)
    {
        _geometry.Clear();
        _blockers.Clear();
        var bounds = args.WorldAABB.Enlarged(LightBloomGeometry.MaxRadius);
        if (args.Viewport.Eye is { } eye)
            bounds = bounds.Union(Box2.CenteredAround(eye.Position.Position, new Vector2(0.01f)));
        _occluders.QueryAabb(_blockers, args.MapId, _transform.GetRenderCullingBounds(args.MapId, bounds));
        foreach (var (uid, blocker, xform) in _blockers)
        {
            if (!blocker.Enabled || xform.MapID != args.MapId || !Eligible(uid))
                continue;
            _geometry.AddBlocker(blocker.Polygon, _transform.GetRenderWorldMatrix((uid, xform)));
        }
    }

    private bool Eligible(EntityUid uid)
    {
        return TryComp(uid, out MetaDataComponent? meta) &&
            (meta.Flags & (MetaDataFlags.Detached | MetaDataFlags.InContainer)) == 0 &&
            meta.EntityLifeStage < EntityLifeStage.Terminating;
    }

    private Vector2 Position(EntityUid uid, TransformComponent xform, PointLightComponent light, out Angle rotation)
    {
        var (position, rot) = _transform.GetRenderWorldPositionRotation((uid, xform));
        rotation = rot;
        return position + rot.RotateVec(light.Offset);
    }

    private void UpdateLampVisibility(in OverlayDrawArgs args)
    {
        CollectBlockers(args);
        var eye = args.Viewport.Eye;
        if (eye == null)
            return;
        _entities.Clear();
        _lookup.GetEntitiesIntersecting(args.MapId, _transform.GetRenderCullingBounds(args.MapId, args.WorldAABB),
            _entities, LookupFlags.Uncontained | LookupFlags.Approximate);
        foreach (var uid in _entities)
        {
            if ((!HasComp<BloomOverlayVisualsComponent>(uid) && !HasComp<PoweredLightVisualsComponent>(uid) &&
                 !HasComp<EmergencyLightComponent>(uid)) || !Eligible(uid) ||
                !TryComp<SpriteComponent>(uid, out var sprite) || !TryComp(uid, out TransformComponent? xform) ||
                !TryComp<PointLightComponent>(uid, out var light))
                continue;
            var position = Position(uid, xform, light, out _);
            var visible = !eye.DrawFov || _geometry.IsVisible(eye.Position.Position, position);
            ApplyLampVisibility(uid, sprite, visible);
        }
    }

    internal void ApplyLampVisibility(EntityUid uid, SpriteComponent sprite, bool visible)
    {
        var index = 0;
        foreach (var layer in sprite.AllLayers)
        {
            if (layer is not SpriteComponent.Layer actual)
            {
                index++;
                continue;
            }
            var key = (uid, index++);
            if (visible)
            {
                if (_hidden.Remove(key, out var old) && ReferenceEquals(actual.Shader, _hiddenShader))
                    RestoreLayer(old);
            }
            else if (!ReferenceEquals(actual.Shader, _hiddenShader))
            {
                _hidden[key] = (sprite, key.Item2, actual, actual.Shader, actual.ShaderPrototype?.Id);
                sprite.LayerSetShader(key.Item2, _hiddenShader);
            }
        }
    }
    private void Restore(EntityUid uid)
    {
        var keys = new List<(EntityUid Uid, int Index)>();
        foreach (var (key, entry) in _hidden)
        {
            if (key.Uid != uid)
                continue;
            RestoreLayer(entry);
            keys.Add(key);
        }
        foreach (var key in keys)
            _hidden.Remove(key);
    }

    private void RestoreLayer((SpriteComponent Sprite, int Index, SpriteComponent.Layer Layer, ShaderInstance? Shader, string? Prototype) entry)
    {
        if (!_sprites.TryGetLayer((entry.Sprite.Owner, entry.Sprite), entry.Index, out var current, false) || !ReferenceEquals(current, entry.Layer) ||
            !ReferenceEquals(entry.Layer.Shader, _hiddenShader))
            return;
        var shader = entry.Prototype == SpriteSystem.UnshadedId.Id
            ? _prototypes.Index(SpriteSystem.UnshadedId).Instance() : entry.Shader;
        entry.Sprite.LayerSetShader(entry.Index, shader, entry.Prototype);
    }

    private void DrawBloom(in OverlayDrawArgs args)
    {
        var eye = args.Viewport.Eye;
        if (eye == null || !eye.DrawLight || !_cfg.GetCVar(CCVars.LightBloomEnabled))
            return;
        var intensity = Math.Clamp(_cfg.GetCVar(CCVars.LightBloomIntensity), 0, 100) / 100f;
        if (intensity <= 0)
            return;

        // Per-viewport state is rebuilt here: secondary viewports never reuse another eye's visibility.
        CollectBlockers(args);
        _candidates.Clear();
        _visible.Clear();
        _lights.QueryAabb(_candidates, args.MapId,
            _transform.GetRenderCullingBounds(args.MapId, args.WorldAABB.Enlarged(LightBloomGeometry.MaxRadius)));
        foreach (var (uid, sharedLight, xform) in _candidates)
        {
            if (HasComp<NoLightBloomComponent>(uid) || sharedLight is not PointLightComponent light || !light.Enabled || light.ContainerOccluded ||
                light.Energy <= 0 || light.Radius <= 0 || light.Color.A <= 0 || !Eligible(uid) ||
                xform.MapID != args.MapId ||
                (TryComp<SpriteComponent>(uid, out var sprite) && (!sprite.Visible || sprite.ContainerOccluded || sprite.Color.A <= 0)))
                continue;
            var position = Position(uid, xform, light, out var rotation);
            if (!args.WorldAABB.Enlarged(LightBloomGeometry.MaxRadius).Contains(position) ||
                (eye.DrawFov && !_geometry.IsVisible(eye.Position.Position, position)))
                continue;
            _visible.Add((uid, light, position, rotation, Vector2.DistanceSquared(eye.Position.Position, position)));
        }
        _visible.Sort(static (a, b) =>
        {
            var distance = a.Distance.CompareTo(b.Distance);
            return distance != 0 ? distance : a.Uid.CompareTo(b.Uid);
        });
        var limit = Math.Clamp(_cfg.GetCVar(CCVars.LightBloomMaxLights), 16, 128);
        var handle = args.WorldHandle;
        handle.SetTransform(Matrix3x2.Identity);
        handle.UseShader(_shader);
        try
        {
            for (var i = 0; i < Math.Min(limit, _visible.Count); i++)
            {
                var (_, light, position, rotation, _) = _visible[i];
                var radius = Math.Min(LightBloomGeometry.MaxRadius, light.Radius * 0.2f);
                var count = _geometry.Build(position, radius, _positions);
                if (count == 0)
                    continue;
                for (var j = 0; j < count; j++)
                    _vertices[j] = new DrawVertexUV2D(_positions[j], (_positions[j] - position) / (2 * radius) + new Vector2(0.5f));
                var mask = light.LightMask is { } maskId
                    ? _resources.GetResource<TextureResource>(_prototypes.Index(maskId).MaskPath).Texture
                    : Texture.White;
                _shader.SetParameter("light_mask", mask);
                _shader.SetParameter("mask_scale", radius / light.Radius);
                var angle = SharedPointLightSystem.GetMaskWorldRotation(light, rotation);
                _shader.SetParameter("mask_rotation", new Vector2((float) Math.Cos(angle.Theta), (float) Math.Sin(angle.Theta)));
                var fade = Math.Clamp((limit - i) / Math.Max(1f, limit * 0.2f), 0, 1);
                var color = light.Color;
                var tint = color.WithAlpha(color.A * intensity * Math.Min(light.Energy, 2f) * 0.35f * fade);
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, mask, _vertices.AsSpan(0, count), tint);
            }
        }
        finally
        {
            handle.UseShader(null);
            handle.SetTransform(Matrix3x2.Identity);
        }
    }

    private sealed class BloomOverlay(LightBloomSystem system) : Overlay
    {
        public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;
        protected override void Draw(in OverlayDrawArgs args) => system.DrawBloom(args);
    }

    private sealed class VisibilityOverlay(LightBloomSystem system) : Overlay
    {
        public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;
        protected override void Draw(in OverlayDrawArgs args) => system.UpdateLampVisibility(args);
    }
}
