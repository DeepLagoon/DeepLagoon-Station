using System.Linq;
using Content.Client.Clothing;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Clothing.Components;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Audio.Components;

namespace Content.Client._DeepLagoon.LustWish;

public sealed partial class LustWishVisualSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private ClientClothingSystem _clothing = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    private LustWishLeashOverlay? _leashOverlay;
    private readonly Dictionary<EntityUid, Color> _palette = new();
    private readonly Dictionary<EntityUid, string> _signatures = new();
    private readonly Dictionary<EntityUid, List<string>> _layers = new();
    private bool _wasDeaf;

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(Robust.Client.Audio.AudioSystem));
        _leashOverlay = new LustWishLeashOverlay();
        _overlays.AddOverlay(_leashOverlay);
        SubscribeLocalEvent<LustWishPaletteComponent, ComponentShutdown>((uid, _, _) => _palette.Remove(uid));
        SubscribeLocalEvent<LustWishWornEffectsComponent, ComponentShutdown>((uid, _, _) => Clear(uid));
    }

    public override void FrameUpdate(float frameTime)
    {
        var paletteQuery = EntityQueryEnumerator<LustWishPaletteComponent, SpriteComponent>();
        while (paletteQuery.MoveNext(out var uid, out var tint, out var sprite))
        {
            if (_palette.TryGetValue(uid, out var old) && old == tint.Color) continue;
            _palette[uid] = tint.Color; sprite.Color = tint.Color;
            if (TryComp<ClothingComponent>(uid, out var clothing) && clothing.InSlot != null)
                _clothing.RefreshLegShape(Transform(uid).ParentUid);
        }
        var query = EntityQueryEnumerator<LustWishWornEffectsComponent, SpriteComponent>();
        while (query.MoveNext(out var wearer, out var gear, out var sprite))
        {
            var signature = string.Join(";", gear.Items.Select(p => p.Key + p.Value +
                (TryGetEntity(p.Value, out var item) && TryComp<LustWishPaletteComponent>(item, out var tint) ? tint.Color.ToHex() : "")));
            if (_signatures.GetValueOrDefault(wearer) == signature && _layers.TryGetValue(wearer, out var previous) &&
                previous.All(key => sprite.LayerMapTryGet(key, out _))) continue;
            Clear(wearer);
            var ready = true;
            var keys = new List<string>(); _layers[wearer] = keys;
            foreach (var (slot, netEntity) in gear.Items)
            {
                if (!TryGetEntity(netEntity, out var item) || item == null) { ready = false; continue; }
                if (!TryComp<LustWishAccessoryComponent>(item, out var accessory) || accessory.WornSprite == null) continue;
                var layer = _sprites.AddLayer((wearer, sprite), accessory.WornSprite, sprite.AllLayers.Count());
                var key = "lustwish/" + slot; sprite.LayerMapSet(key, layer); keys.Add(key);
                if (TryComp<LustWishPaletteComponent>(item, out var tint)) sprite.LayerSetColor(layer, tint.Color);
            }
            if (ready) _signatures[wearer] = signature;
        }
        var deaf = TryComp<LustWishWornEffectsComponent>(_players.LocalEntity, out var worn) && worn.Deaf;
        if (deaf || _wasDeaf)
        {
            var audioQuery = EntityQueryEnumerator<AudioComponent>();
            while (audioQuery.MoveNext(out _, out var audio))
                if (!audio.Global) audio.Volume = audio.Params.Volume + (deaf ? -30 : 0);
        }
        _wasDeaf = deaf;
    }

    private void Clear(EntityUid uid)
    {
        _signatures.Remove(uid);
        if (!_layers.Remove(uid, out var layers) || !TryComp<SpriteComponent>(uid, out var sprite)) return;
        foreach (var key in layers)
            if (sprite.LayerMapTryGet(key, out var index)) _sprites.RemoveLayer((uid, sprite), index);
    }

    public override void Shutdown()
    {
        if (_leashOverlay != null) { _overlays.RemoveOverlay(_leashOverlay); _leashOverlay.Dispose(); _leashOverlay = null; }
        base.Shutdown();
    }
}

public sealed partial class LustWishLeashOverlay : Overlay
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;
    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public LustWishLeashOverlay() => IoCManager.InjectDependencies(this);
    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entities.TryGetComponent(_players.LocalEntity, out EyeComponent? eye) || args.Viewport.Eye != eye.Eye) return;
        var transforms = _entities.System<SharedTransformSystem>();
        var query = _entities.EntityQueryEnumerator<LustWishWornEffectsComponent, TransformComponent>();
        while (query.MoveNext(out var wearer, out var gear, out var xform))
        {
            if (gear.LeashHolder is not { } net || !_entities.TryGetEntity(net, out var holder) || holder == null ||
                wearer != _players.LocalEntity && holder != _players.LocalEntity || xform.MapID != args.MapId ||
                !_entities.TryGetComponent(holder, out TransformComponent? holderTransform)) continue;
            args.WorldHandle.DrawLine(transforms.GetWorldPosition(wearer), transforms.GetWorldPosition(holder.Value), Color.FromHex("#AC70D4"));
        }
    }
}
