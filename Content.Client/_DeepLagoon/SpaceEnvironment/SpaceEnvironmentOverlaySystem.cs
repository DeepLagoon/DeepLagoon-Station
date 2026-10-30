using System.Numerics;
using Content.Client.Parallax;
using Content.Client._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Helpers;
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
    private readonly Dictionary<EntityUid, (PlanetarySystem System, Dictionary<Planet, ShaderInstance?> Shaders)> _planetShaders = new();
    private readonly Dictionary<EntityUid, (SectorDefinition Definition, Vector2[][] Contours, DrawVertexUV2DColor[][] Fills)> _contours = new();

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new SpaceEnvironmentOverlay(EntityManager, _prototypes, _cfg);
        _overlays.AddOverlay(_overlay);
        SubscribeLocalEvent<SpaceEnvironmentComponent, ComponentShutdown>(OnEnvironmentShutdown);
        SubscribeLocalEvent<StarSystemMapComponent, ComponentShutdown>(OnStarSystemShutdown);
    }

    public override void Shutdown()
    {
        if (_overlay != null) _overlays.RemoveOverlay(_overlay);
        _overlay = null;
        _contours.Clear();
        foreach (var map in _planetShaders.Keys) DisposePlanetShaders(map);
        _planetShaders.Clear();
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

    /// <summary>Only small portrait quads run the native planet shader; instances are reused by both consoles.</summary>
    public ShaderInstance? PlanetShader(EntityUid map, PlanetarySystem system, Planet planet)
    {
        if (!_planetShaders.TryGetValue(map, out var cache) || !ReferenceEquals(cache.System, system))
        {
            DisposePlanetShaders(map);
            cache = (system, new Dictionary<Planet, ShaderInstance?>());
            _planetShaders[map] = cache;
        }
        if (cache.Shaders.TryGetValue(planet, out var existing)) return existing;
        if (cache.Shaders.Count >= SectorGenerator.MaxPlanets) return null;
        var shader = PlanetShaderFactory.Create(_prototypes, planet, system.Star);
        if (shader != null)
        {
            // Recenter the virtual viewport to avoid precision loss in distant sectors.
            var extent = planet.Radius * MathF.Max(1.3f, (planet.Rings?.RadiusOuter ?? 1f) * 1.3f);
            shader.SetParameter("planetPos", Vector2.Zero);
            shader.SetParameter("starPos", system.Star.Position - planet.Position);
            shader.SetParameter("planetaryRadiusFactor", 1f);
            shader.SetParameter("parallaxCenter", Vector2.Zero);
            shader.SetParameter("viewportMin", new Vector2(-extent, -extent));
            shader.SetParameter("viewportSize", new Vector2(extent * 2));
        }
        cache.Shaders.Add(planet, shader);
        return shader;
    }

    private void DisposePlanetShaders(EntityUid map)
    {
        if (!_planetShaders.TryGetValue(map, out var cache)) return;
        foreach (var shader in cache.Shaders.Values) shader?.Dispose();
        cache.Shaders.Clear();
    }

    private void OnStarSystemShutdown(EntityUid uid, StarSystemMapComponent component, ComponentShutdown args)
    {
        DisposePlanetShaders(uid);
        _planetShaders.Remove(uid);
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
