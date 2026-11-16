using System.Numerics;
using Content.Shared._DeepLagoon.Apartments;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.Apartments;

public sealed class ApartmentFloorFinishSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ApartmentFloorFinishComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(Entity<ApartmentFloorFinishComponent> ent, ref ComponentStartup args)
    {
        if (TryComp<SpriteComponent>(ent, out var sprite) && Texture(ent.Comp) is { } texture)
            _sprites.LayerSetTexture((ent.Owner, sprite), 0, texture);
    }

    public Texture? Texture(ApartmentFloorFinishComponent finish)
    {
        if (_prototypes.Index(finish.Tile).Sprite is not { } sprite ||
            !_resources.TryGetResource<TextureResource>(new ResPath("/Textures") / sprite, out var resource))
            return null;

        var texture = resource.Texture;
        if (texture.Width == 32 && texture.Height == 32)
            return texture;

        return new AtlasTexture(texture, UIBox2.FromDimensions(Vector2.Zero,
            new Vector2(Math.Min(32, texture.Width), Math.Min(32, texture.Height))));
    }
}
