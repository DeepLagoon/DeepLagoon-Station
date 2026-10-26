using System.Linq;
using Content.Shared._DeepLagoon.LustWish;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.LustWish;

public sealed partial class LustWishPortalVisualSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private const string ClipPass = "lustwish-portal-half";
    private readonly Dictionary<EntityUid, ShaderInstance> _shaders = new();
    private readonly Dictionary<EntityUid, string> _copies = new();

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(LustWishVisualSystem));
        SubscribeLocalEvent<LustWishPortalOccupantComponent, ComponentShutdown>((uid, _, _) => Clear(uid));
        SubscribeLocalEvent<LustWishProjectionComponent, ComponentShutdown>((uid, _, _) => { Clear(uid); _copies.Remove(uid); });
    }

    public override void FrameUpdate(float frameTime)
    {
        var bodies = EntityQueryEnumerator<LustWishPortalOccupantComponent, SpriteComponent>();
        while (bodies.MoveNext(out var body, out _, out var sprite)) Clip(body, sprite, false);
        var query = EntityQueryEnumerator<LustWishProjectionComponent, SpriteComponent>();
        while (query.MoveNext(out var proxy, out var projection, out var sprite))
        {
            if (!TryComp<SpriteComponent>(projection.Body, out var original)) { sprite.Visible = false; continue; }
            var signature = original.Color + "/" + original.Scale + string.Join(";", original.AllLayers.Select(l =>
                l.ActualRsi?.Path + "/" + l.RsiState + "/" + l.Visible + "/" + l.Color + "/" + l.Scale + "/" + l.Rotation));
            if (_copies.GetValueOrDefault(proxy) != signature)
            {
                _sprites.CopySprite((projection.Body, original), (proxy, sprite));
                _sprites.RemovePostShader(sprite, ClipPass);
                _copies[proxy] = signature;
            }
            sprite.Visible = original.Visible;
            Clip(proxy, sprite, true);
        }
    }

    private void Clip(EntityUid uid, SpriteComponent sprite, bool lower)
    {
        if (!_shaders.TryGetValue(uid, out var shader))
        {
            shader = _prototypes.Index<ShaderPrototype>("LustWishPortalHalf").InstanceUnique();
            _shaders[uid] = shader;
        }
        var bounds = _sprites.GetLocalBounds((uid, sprite));
        shader.SetParameter("split", bounds.Height > 0 ? Math.Clamp(bounds.Top / bounds.Height, 0, 1) : .5f);
        shader.SetParameter("lower", lower ? 1f : 0f);
        _sprites.SetPostShader((uid, sprite), new SpriteComponent.PostShaderArgs(ClipPass, shader));
    }

    private void Clear(EntityUid uid)
    {
        if (TryComp<SpriteComponent>(uid, out var sprite)) _sprites.RemovePostShader(sprite, ClipPass);
        if (_shaders.Remove(uid, out var shader)) shader.Dispose();
    }

    public override void Shutdown()
    {
        foreach (var uid in _shaders.Keys.ToArray()) Clear(uid);
        _copies.Clear(); base.Shutdown();
    }
}
