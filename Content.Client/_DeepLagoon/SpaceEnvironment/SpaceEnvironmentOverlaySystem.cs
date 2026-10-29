using System.Numerics;
using Content.Client.Parallax;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.SpaceEnvironment;

[CVarDefs]
public sealed class SpaceEnvironmentClientCVars
{
    public static readonly CVarDef<bool> Visuals = CVarDef.Create("render.sector_environment", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}

public sealed class SpaceEnvironmentOverlaySystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    private SpaceEnvironmentOverlay? _overlay;
    private readonly Dictionary<EntityUid, (SectorDefinition Definition, Vector2[][] Contours, DrawVertexUV2DColor[][] Fills)> _contours = new();

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new SpaceEnvironmentOverlay(EntityManager, _prototypes, _cfg);
        _overlays.AddOverlay(_overlay);
        SubscribeLocalEvent<SpaceEnvironmentComponent, ComponentShutdown>(OnEnvironmentShutdown);
    }

    public override void Shutdown()
    {
        if (_overlay != null) _overlays.RemoveOverlay(_overlay);
        _overlay = null;
        _contours.Clear();
        base.Shutdown();
    }
    public Vector2[][] Contours(EntityUid map, SectorDefinition definition)
        => NavigationGeometry(map, definition).Contours;

    public DrawVertexUV2DColor[][] RadarFills(EntityUid map, SectorDefinition definition)
        => NavigationGeometry(map, definition).Fills;

    private (SectorDefinition Definition, Vector2[][] Contours, DrawVertexUV2DColor[][] Fills) NavigationGeometry(EntityUid map, SectorDefinition definition)
    {
        if (_contours.TryGetValue(map, out var cached) && ReferenceEquals(cached.Definition, definition)) return cached;
        var contours = new Vector2[definition.Regions.Count][];
        var fills = new DrawVertexUV2DColor[definition.Regions.Count][];
        for (var index = 0; index < contours.Length; index++)
        {
            contours[index] = new Vector2[SpaceRegionGeometry.ContourSegments];
            for (var point = 0; point < contours[index].Length; point++)
                contours[index][point] = SpaceRegionGeometry.ContourPoint(definition.Regions[index], point);
            fills[index] = SpaceRegionRadarMesh.Build(definition.Regions[index], contours[index]);
        }
        var geometry = (definition, contours, fills);
        _contours[map] = geometry;
        return geometry;
    }

    private void OnEnvironmentShutdown(EntityUid uid, SpaceEnvironmentComponent component, ComponentShutdown args)
        => _contours.Remove(uid);
}

public sealed class SpaceEnvironmentOverlay : Overlay
{
    private readonly IEntityManager _entities;
    private readonly IPrototypeManager _prototypes;
    private readonly IConfigurationManager _cfg;
    private ShaderInstance? _shader;
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    public SpaceEnvironmentOverlay(IEntityManager entities, IPrototypeManager prototypes, IConfigurationManager cfg)
    {
        _entities = entities;
        _prototypes = prototypes;
        _cfg = cfg;
        ZIndex = ParallaxSystem.ParallaxZIndex + 2;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_cfg.GetCVar(SpaceEnvironmentClientCVars.Visuals)
            || !_entities.TryGetComponent<SpaceEnvironmentComponent>(args.MapUid, out var environment)) return;
        _shader ??= _prototypes.Index<ShaderPrototype>("DeepLagoonSpaceCloud").InstanceUnique();
        var handle = args.WorldHandle;
        var drawn = 0;
        foreach (var region in environment.Definition.Regions)
        {
            var center = new Vector2(region.X, region.Y);
            var bounds = Box2.CenteredAround(center, new Vector2(region.Radius * 2));
            if (!args.WorldAABB.Intersects(bounds)) continue;
            _shader.SetParameter("cloudColor", SpaceRegionGeometry.Color(region.Kind));
            _shader.SetParameter("cloudSeed", (region.Seed & 65535) * 0.001f);
            _shader.SetParameter("cloudDensity", region.Intensity);
            handle.UseShader(_shader);
            var matrix = Matrix3x2.CreateScale(new Vector2(region.Radius, region.Radius * region.Aspect))
                * Matrix3x2.CreateRotation(region.Rotation) * Matrix3x2.CreateTranslation(center);
            handle.SetTransform(matrix);
            handle.DrawTextureRect(Texture.White, Box2.CenteredAround(Vector2.Zero, new Vector2(2)));
            if (++drawn >= 12) break;
        }
        handle.UseShader(null);
        handle.SetTransform(Matrix3x2.Identity);
    }

    protected override void DisposeBehavior()
    {
        _shader?.Dispose();
        _shader = null;
        base.DisposeBehavior();
    }
}
