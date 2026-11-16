using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.Modsuits;

public sealed partial class ModHardlightVisualSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;

    private readonly Dictionary<ModHardlightEffect, ShaderInstance> _effects = new();
    private static readonly HumanoidVisualLayers[] BodyLayers = Enum.GetValues<HumanoidVisualLayers>();
    private static readonly string[] BodyFeatureSides = ["FRONT", "BEHIND"];
    private float _refreshIn;
    private static readonly ProtoId<ShaderPrototype> HardlightShader = "ModHardlight";

    public override void Initialize()
    {
        UpdatesOutsidePrediction = true;
        SubscribeLocalEvent<ModHardlightVisualComponent, ComponentShutdown>(OnShutdown);
    }

    public override void FrameUpdate(float frameTime)
    {
        _refreshIn -= frameTime;
        if (_refreshIn > 0)
            return;

        _refreshIn = 0.10f;
        var query = EntityQueryEnumerator<ModHardlightVisualComponent, HumanoidAppearanceComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var effect, out var humanoid, out var sprite))
        {
            if (!sprite.Visible)
                continue;

            ApplyVisual(humanoid, sprite, effect.Parts, effect.Color, effect.Effect, EnsureComp<ModHardlightLayersComponent>(uid));
        }
    }

    public EntityUid? CreatePreview(EntityUid? wearer)
    {
        if (wearer is not {} uid || !TryComp<SpriteComponent>(uid, out var source))
            return null;

        var preview = Spawn(null, Robust.Shared.Map.MapCoordinates.Nullspace);
        var target = EnsureComp<SpriteComponent>(preview);
        EntityManager.System<SpriteSystem>().CopySprite((uid, source), (preview, target));
        for (var i = target.LayerData.Count - 1; i >= 0; i--)
        {
            if (target.LayerData[i].Shader is {} shader && _effects.ContainsValue(shader))
                target.RemoveLayer(i);
        }

        return preview;
    }

    public void UpdatePreview(EntityUid? wearer, EntityUid? preview, ModHardlightParts parts, Color color, ModHardlightEffect effect)
    {
        if (preview is not {} uid ||
            !TryComp<HumanoidAppearanceComponent>(wearer, out var humanoid) ||
            !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        ApplyVisual(humanoid, sprite, parts, color, effect, EnsureComp<ModHardlightLayersComponent>(uid));
    }

    public void DeletePreview(EntityUid? preview) => QueueDel(preview);

    private void ApplyVisual(HumanoidAppearanceComponent humanoid, SpriteComponent sprite, ModHardlightParts parts,
        Color color, ModHardlightEffect effect, ModHardlightLayersComponent client)
    {
        foreach (var layer in BodyLayers)
        {
            var region = ModHardlightRegions.FromBaseLayer(layer);
            if (region == ModHardlightParts.None)
                continue;

            UpdateLayer(sprite, layer, "mod-hardlight/body/" + layer,
                (parts & region) != 0 && !humanoid.PermanentlyHidden.Contains(layer), color, effect);
        }

        client.Current.Clear();
        foreach (var (region, feature) in ModHardlightRegions.BodyFeatures)
        {
            if ((parts & region) == 0)
                continue;

            foreach (var side in BodyFeatureSides)
            {
                var source = $"dl-body/{feature}/{side}";
                var key = $"mod-hardlight/feature/{source}";
                client.Current.Add(key);
                UpdateLayer(sprite, source, key, true, color, effect);
            }
        }

        foreach (var markings in humanoid.MarkingSet.Markings.Values)
        {
            foreach (var marking in markings)
            {
                if (!marking.Visible || !_prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype))
                    continue;

                var region = ModHardlightRegions.FromMarking(prototype);
                if ((region & parts) == 0 || humanoid.PermanentlyHidden.Contains(prototype.BodyPart))
                    continue;

                foreach (var entry in prototype.Sprites)
                {
                    if (entry is not SpriteSpecifier.Rsi markingRsi)
                        continue;
                    var source = $"{prototype.ID}-{markingRsi.RsiState}";
                    var key = $"mod-hardlight/marking/{source}";
                    client.Current.Add(key);
                    UpdateLayer(sprite, source, key, true, color, effect);
                }
            }
        }

        foreach (var key in client.Markings)
        {
            if (!client.Current.Contains(key) && sprite.LayerMapTryGet(key, out var index))
                sprite.RemoveLayer(index);
        }

        (client.Markings, client.Current) = (client.Current, client.Markings);
    }

    private void UpdateLayer(SpriteComponent sprite, object source, string key, bool enabled, Color color, ModHardlightEffect effect)
    {
        var exists = sprite.LayerMapTryGet(key, out var destination);
        if (!enabled || !sprite.LayerMapTryGet(source, out var index) ||
            !sprite.TryGetLayer(index, out var body) || !body.State.IsValid && body.Texture == null)
        {
            if (exists)
                sprite.RemoveLayer(destination);
            return;
        }

        if (!exists)
        {
            destination = sprite.AddBlankLayer();
            sprite.LayerMapSet(key, destination);
        }

        var current = sprite.LayerData[destination];
        if (current.State != body.State || current.ActualRsi != body.ActualRsi ||
            !body.State.IsValid && current.Texture != body.Texture)
        {
            if (body.State.IsValid)
                sprite.LayerSetState(destination, body.State, body.ActualRsi);
            else
                sprite.LayerSetTexture(destination, body.Texture);
        }

        if (current.Offset != body.Offset)
            sprite.LayerSetOffset(destination, body.Offset);
        if (current.Scale != body.Scale)
            sprite.LayerSetScale(destination, body.Scale);
        if (current.Rotation != body.Rotation)
            sprite.LayerSetRotation(destination, body.Rotation);
        if (current.DirOffset != body.DirOffset)
            sprite.LayerSetDirOffset(destination, body.DirOffset);
        if (current.RenderingStrategy != body.RenderingStrategy)
            sprite.LayerSetRenderingStrategy(destination, body.RenderingStrategy);
        if (!current.Visible)
            sprite.LayerSetVisible(destination, true);

        if (current.Color != color)
            sprite.LayerSetColor(destination, color);
        var shader = GetShader(effect);
        if (!ReferenceEquals(current.Shader, shader))
            sprite.LayerSetShader(destination, shader);
    }

    private ShaderInstance GetShader(ModHardlightEffect effect)
    {
        if (_effects.TryGetValue(effect, out var existing))
            return existing;

        var (pattern, rate) = effect switch
        {
            ModHardlightEffect.Static => ("static_base", 10f),
            ModHardlightEffect.Scanline => ("scanline", 5f),
            ModHardlightEffect.Wave => ("wave", 10f),
            ModHardlightEffect.Field => ("field", 10f),
            ModHardlightEffect.Hieroglyphs => ("hiero", 10f),
            _ => ("anim_tg", 10f),
        };
        var shader = _prototypes.Index(HardlightShader).InstanceUnique();
        var texture = _resources.GetResource<TextureResource>(new ResPath($"/Textures/_DeepLagoon/Modsuits/hardlight/{pattern}.png")).Texture;
        shader.SetParameter("pattern", texture);
        shader.SetParameter("frames", (float) (texture.Width / 64));
        shader.SetParameter("frame_rate", rate);
        _effects.Add(effect, shader);
        return shader;
    }

    private void OnShutdown(Entity<ModHardlightVisualComponent> ent, ref ComponentShutdown args)
    {
        if (TryComp<SpriteComponent>(ent, out var sprite))
        {
            foreach (var layer in BodyLayers)
            {
                if (sprite.LayerMapTryGet("mod-hardlight/body/" + layer, out var index))
                    sprite.RemoveLayer(index);
            }

            if (TryComp<ModHardlightLayersComponent>(ent, out var client))
            {
                foreach (var key in client.Markings)
                {
                    if (sprite.LayerMapTryGet(key, out var index))
                        sprite.RemoveLayer(index);
                }
            }
        }

        RemCompDeferred<ModHardlightLayersComponent>(ent);
    }
}

[RegisterComponent]
public sealed partial class ModHardlightLayersComponent : Component
{
    public HashSet<string> Markings = new();
    public HashSet<string> Current = new();
}
