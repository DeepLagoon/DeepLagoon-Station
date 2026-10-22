using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;
using Robust.Shared.Configuration;
using Robust.Client.Player;
using Robust.Shared.Player;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._DeepLagoon.Tattoos;

/// <summary>Ink draws over markings and cosmetic anatomy. Luminous ink uses the same masks and visibility.</summary>
public sealed partial class TattooVisualizerSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    private sealed class Visuals
    {
        public string Key = "";
        public readonly List<(string Key, HumanoidVisualLayers Part)> Layers = new();
        public readonly List<OwnedTexture> Textures = new();
        public readonly List<ShaderInstance> Shaders = new();
    }
    private readonly Dictionary<EntityUid, Visuals> _visuals = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<HumanoidAppearanceComponent, ComponentShutdown>((uid, _, _) => Clear(uid));
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(_ => RefreshVisibility());
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => RefreshVisibility());
        _cfg.OnValueChanged(TattooCVars.Enabled, OnPreferenceChanged);
    }

    private void OnPreferenceChanged(bool _) => RefreshVisibility();
    private void RefreshVisibility()
    {
        foreach (var uid in _visuals.Keys)
            if (TryComp<HumanoidAppearanceComponent>(uid, out var humanoid) && TryComp<SpriteComponent>(uid, out var sprite))
                UpdateVisibility(uid, humanoid, sprite);
    }

    public static string Fingerprint(IEnumerable<Tattoo> tattoos) => string.Join(';', tattoos.Select(t =>
        $"{t.Id}/{t.BodyPart}/{t.Direction}/{F(t.X)}/{F(t.Y)}/{F(t.Scale)}/{F(t.Rotation)}/{F(t.Opacity)}/" +
        string.Join('|', t.Strokes.Select(s => $"{s.Color}/{F(s.Width)}/{s.Glow}/{s.Erase}/{Points(s)}"))
        + $"/{t.Mirrored}/{t.NormalBitmap}/{t.GlowBitmap}"));
    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Points(TattooStroke stroke) => string.Join(';', stroke.Points.Select(p => F(p.X) + "," + F(p.Y)));

    public void Refresh(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        var tattoos = Tattoo.Sanitize(humanoid.Tattoos);
        var key = Fingerprint(tattoos) + string.Join(';', tattoos.Select(t =>
            sprite.LayerMapTryGet(t.BodyPart, out var index) ? $"{sprite[index].ActualRsi?.Path}/{sprite[index].RsiState}" : "missing"))
            + "/anatomy/" + string.Join(';', humanoid.BodyFeatures.Select(f => $"{f.Part}/{f.Enabled}/{f.Shape}/{f.Size}/{f.Visibility}/{f.SecondaryEnabled}/{f.SecondaryColor}"));
        var anatomy = EntityManager.System<Content.Client._DeepLagoon.BodyCustomization.BodyCustomizationVisualizerSystem>();
        var anatomyEnd = anatomy.LastLayerIndex(uid, sprite);
        var lowerBounds = tattoos.Select(t => t.BodyPart).Distinct().ToDictionary(part => part,
            part => Math.Max(anatomyEnd, TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypes, part).DefaultIfEmpty(-1).Max()));
        if (_visuals.TryGetValue(uid, out var cached) && cached.Key == key
            && cached.Layers.All(l => sprite.LayerMapTryGet(l.Key, out var index) && index > lowerBounds[l.Part]))
        {
            UpdateVisibility(uid, humanoid, sprite);
            return;
        }
        Clear(uid);
        if (tattoos.Count == 0) return;
        var visuals = new Visuals { Key = key };
        _visuals[uid] = visuals;
        try
        {
            foreach (var tattoo in tattoos)
            {
                if (!humanoid.BaseLayers.TryGetValue(tattoo.BodyPart, out var body) || !body.AllowsMarkings
                    || !sprite.LayerMapTryGet(tattoo.BodyPart, out var target)) continue;
                var source = (SpriteComponent.Layer) sprite[target];
                if (source.ActualRsi == null || source.ActualState is not { } state) continue;
                // Skin RSI states are small. Keep custom/animated resources bounded too.
                if (state.Size.X > 64 || state.Size.Y > 64 || state.DelayCount > 32) continue;
                foreach (var glow in new[] { false, true })
                {
                    if (!tattoo.Strokes.Any(s => !s.Erase && s.Glow == glow)
                        && (glow ? tattoo.GlowBitmap : tattoo.NormalBitmap).Length == 0) continue;
                    var count = state.RsiDirections switch { RsiDirectionType.Dir1 => 1, RsiDirectionType.Dir4 => 4, _ => 8 };
                    if (tattoo.Direction >= count) continue;
                    // Placement stays high resolution; the shader masks against the CURRENT skin
                    // frame. Reuse the engine's directional/animated RSI rather than replacing it.
                    using var canvas = new Image<Rgba32>(state.Size.X, state.Size.Y, new Rgba32(255, 255, 255, 255));
                    using var ink = TattooRasterizer.Render(tattoo, glow, canvas);
                    var texture = Upload(ink, visuals);
                    var anatomyLayers = anatomy.LayerIndices(uid, sprite, tattoo.BodyPart);
                    var anatomyTexture = texture;
                    if (anatomyLayers.Length > 0)
                    {
                        using var anatomyMask = TattooBodyPreview.ReadAnatomy(sprite, target, tattoo.Direction, anatomyLayers);
                        anatomyTexture = Upload(anatomyMask, visuals);
                    }
                    var shaderId = glow ? "DeepLagoonTattooGlow" : "DeepLagoonTattooInk";
                    var shader = _prototypes.Index<ShaderPrototype>(shaderId).InstanceUnique();
                    visuals.Shaders.Add(shader);
                    shader.SetParameter("inkTexture", texture);
                    shader.SetParameter("anatomyMask", anatomyTexture);
                    shader.SetParameter("anatomyMaskEnabled", anatomyLayers.Length > 0 ? 1f : 0f);
                    var min = new Vector2[32];
                    var max = new Vector2[32];
                    for (var frame = 0; frame < state.DelayCount; frame++)
                    {
                        var bodyFrame = state.GetFrame(unchecked((RsiDirection) tattoo.Direction), frame);
                        var region = bodyFrame is AtlasTexture atlas ? atlas.SubRegion : new UIBox2(0, 0, bodyFrame.Width, bodyFrame.Height);
                        var backing = bodyFrame is AtlasTexture a ? a.SourceTexture : bodyFrame;
                        min[frame] = new Vector2(region.Left / backing.Width, 1 - region.Bottom / backing.Height);
                        max[frame] = new Vector2(region.Right / backing.Width, 1 - region.Top / backing.Height);
                    }
                    shader.SetParameter("directionMin", min);
                    shader.SetParameter("directionMax", max);
                    shader.SetParameter("directionCount", state.DelayCount);
                    shader.SetParameter("inkOpacity", 1f);
                    var layerKey = "dl-tattoo/" + tattoo.Id + "/" + glow;
                    sprite.LayerMapTryGet(tattoo.BodyPart, out target);
                    // Recompute live indices: removing cached ink shifts anatomy and markings.
                    // Parts configured above clothing also keep their ink above those clothes.
                    var insertion = Math.Max(target + 1, Math.Max(anatomy.LastLayerIndex(uid, sprite),
                        TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypes, tattoo.BodyPart).DefaultIfEmpty(-1).Max()) + 1);
                    foreach (var (previousKey, previousPart) in visuals.Layers)
                        if (previousPart == tattoo.BodyPart && sprite.LayerMapTryGet(previousKey, out var previousIndex))
                            insertion = Math.Max(insertion, previousIndex + 1);
                    // Official Clyde caches uniforms per program but not global sampler bindings.
                    // A separate, transparent instance of this SAME program forces LastInstance
                    // to change before every ink draw, including multiple viewports in one frame.
                    // Updating parameters once per frame cannot guarantee that ordering.
                    var guard = shader.Duplicate();
                    guard.SetParameter("inkOpacity", 0f);
                    visuals.Shaders.Add(guard);
                    var guardKey = layerKey + "/sampler-guard";
                    var guardLayer = new SpriteComponent.Layer(source, sprite) { Color = Color.White.WithAlpha(source.Color.A) };
                    var guardIndex = _sprites.AddLayer((uid, sprite), guardLayer, insertion);
                    sprite.LayerMapSet(guardKey, guardIndex);
                    sprite.LayerSetShader(guardIndex, guard, shaderId);
                    visuals.Layers.Add((guardKey, tattoo.BodyPart));
                    var layer = new SpriteComponent.Layer(source, sprite) { Color = Color.White.WithAlpha(source.Color.A) };
                    var index = _sprites.AddLayer((uid, sprite), layer, insertion + 1);
                    sprite.LayerMapSet(layerKey, index);
                    sprite.LayerSetShader(index, shader, shaderId);
                    visuals.Layers.Add((layerKey, tattoo.BodyPart));
                }
            }
            UpdateVisibility(uid, humanoid, sprite);
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not render tattoos on {ToPrettyString(uid)}: {exception.Message}");
            Clear(uid);
        }
    }

    private static OwnedTexture Upload(Image<Rgba32> image, Visuals visuals)
    {
        var texture = (OwnedTexture) Texture.LoadFromImage(image, "tattoo", new TextureLoadParameters
        { Srgb = true, SampleParameters = new TextureSampleParameters { Filter = true } });
        visuals.Textures.Add(texture);
        return texture;
    }

    public void UpdateVisibility(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        if (!_visuals.TryGetValue(uid, out var visuals)) return;
        foreach (var (key, part) in visuals.Layers)
        {
            var allowed = _players.LocalEntity == uid || (_cfg.GetCVar(TattooCVars.Enabled) && humanoid.TattoosEnabled);
            var visible = allowed && !humanoid.HiddenLayers.ContainsKey(part) && !humanoid.PermanentlyHidden.Contains(part)
                && humanoid.BaseLayers.TryGetValue(part, out var body) && body.AllowsMarkings
                && sprite.LayerMapTryGet(part, out var target) && sprite[target].Visible;
            if (sprite.LayerMapTryGet(key, out var index)) _sprites.LayerSetVisible((uid, sprite), index, visible);
        }
    }

    private void Clear(EntityUid uid)
    {
        if (!_visuals.Remove(uid, out var visuals)) return;
        if (TryComp<SpriteComponent>(uid, out var sprite))
            foreach (var (key, _) in visuals.Layers)
                if (sprite.LayerMapTryGet(key, out var index)) _sprites.RemoveLayer((uid, sprite), index);
        foreach (var texture in visuals.Textures) texture.Dispose();
        foreach (var shader in visuals.Shaders) shader.Dispose();
    }

    public override void Shutdown()
    {
        _cfg.UnsubValueChanged(TattooCVars.Enabled, OnPreferenceChanged);
        foreach (var uid in _visuals.Keys.ToArray()) Clear(uid);
        base.Shutdown();
    }
}
