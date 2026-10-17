using System.Numerics;
using System.IO;
using Content.Shared._DeepLagoon.Art;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.Art;

/// <summary>Normal sprite layers: visible in the world, inventory and SpriteView, using the stock client texture API.</summary>
public sealed class ArtDisplaySystem : EntitySystem
{
    private readonly Dictionary<EntityUid, (string Png, OwnedTexture Texture)> _textures = new();
    private const string Layer = "art-image";
    public override void Initialize()
    {
        SubscribeLocalEvent<ArtDisplayComponent, AfterAutoHandleStateEvent>((uid, c, _) => UpdateSprite(uid, c));
        SubscribeLocalEvent<ArtDisplayComponent, ComponentShutdown>((uid, _, _) => Clear(uid));
    }
    private void Clear(EntityUid uid)
    {
        if (!_textures.Remove(uid, out var old)) return;
        if (TryComp<SpriteComponent>(uid, out var sprite) && sprite.LayerMapTryGet(Layer, out var index)) sprite.LayerSetVisible(index, false);
        old.Texture.Dispose();
    }
    private void UpdateSprite(EntityUid uid, ArtDisplayComponent c)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite) || !sprite.LayerMapTryGet(Layer, out var index)) return;
        if (_textures.TryGetValue(uid, out var old) && old.Png == c.Preview) return;
        if (c.Preview.Length == 0) { Clear(uid); sprite.LayerSetVisible(index, false); return; }
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(c.Preview));
            using var image = Image.Load<Rgba32>(input);
            if (image.Width != ArtLimits.ThumbnailSize || image.Height != ArtLimits.ThumbnailSize) return;
            var texture = (OwnedTexture)Texture.LoadFromImage(image, "artwork", new TextureLoadParameters
                { Srgb = true, SampleParameters = new TextureSampleParameters { Filter = true } });
            sprite.LayerSetTexture(index, texture);
            sprite.LayerSetScale(index, c.DrawingSize / ArtLimits.ThumbnailSize);
            sprite.LayerSetVisible(index, true);
            _textures[uid] = (c.Preview, texture);
            old.Texture?.Dispose();
        }
        catch (Exception e) { Log.Warning($"Artwork preview failed: {e.GetType().Name}"); }
    }
    public override void Shutdown()
    {
        foreach (var value in _textures.Values) value.Texture.Dispose();
        _textures.Clear(); base.Shutdown();
    }
}
