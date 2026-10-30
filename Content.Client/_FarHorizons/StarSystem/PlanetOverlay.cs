using System.Numerics;
using Content.Client.Parallax;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._FarHorizons.StarSystem;

public sealed class PlanetOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly IPrototypeManager _protoMan;
    private readonly List<(Planet Planet, ShaderInstance Shader)> _shaders = new(); // what do you mean, this isn't No Man's Sky?
    private List<Planet>? _planets;
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    public PlanetOverlay(IEntityManager entMan, IPrototypeManager protoMan)
    {
        ZIndex = ParallaxSystem.ParallaxZIndex + 1;
        _entMan = entMan;
        _protoMan = protoMan;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entMan.TryGetComponent<StarSystemMapComponent>(args.MapUid, out var starSystem) ||
            starSystem.StarSystem is not { } system ||
            system.Planets.Count == 0)
        {
            ResetShader();
            return false;
        }

        if (_planets != system.Planets)
        {
            ResetShader();
            _planets = system.Planets;

            foreach (var planet in _planets)
            {
                if (PlanetShaderFactory.Create(_protoMan, planet, system.Star) is { } shader)
                    _shaders.Add((planet, shader));
            }
        }

        var origin = args.WorldAABB.Center;

        _shaders.Sort((a, b) =>
            (origin - b.Planet.Position).LengthSquared().CompareTo((origin - a.Planet.Position).LengthSquared()));

        return _shaders.Count > 0;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        var viewportBounds = args.WorldAABB;
        var parallaxCenter = args.Viewport.Eye?.Position.Position ?? viewportBounds.Center;
        foreach (var (_, shader) in _shaders)
        {
            shader.SetParameter("viewportMin", viewportBounds.BottomLeft);
            shader.SetParameter("viewportSize", viewportBounds.Size);
            shader.SetParameter("parallaxCenter", parallaxCenter);

            handle.UseShader(shader);
            handle.DrawRect(viewportBounds, Color.White);
        }
        handle.UseShader(null);
    }

    protected override void DisposeBehavior()
    {
        ResetShader();
        base.DisposeBehavior();
    }

    public void ResetShader()
    {
        _planets = null;
        foreach (var (_, shader) in _shaders) shader.Dispose();
        _shaders.Clear();
    }
}
