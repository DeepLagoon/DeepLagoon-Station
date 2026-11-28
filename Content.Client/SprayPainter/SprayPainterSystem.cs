using Content.Shared.SprayPainter;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Utility;
using System.Linq;
using Robust.Shared.Graphics;
using Content.Client.SprayPainter.UI;
using Content.Shared.SprayPainter.Components;
using Robust.Client.GameObjects;
using Content.Shared.Doors.Components;

namespace Content.Client.SprayPainter;

public sealed partial class SprayPainterSystem : SharedSprayPainterSystem
{
    [Dependency] private IResourceCache _resourceCache = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    public List<SprayPainterEntry> Entries { get; private set; } = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SprayPainterComponent, AfterAutoHandleStateEvent>(OnPainterState);
        SubscribeLocalEvent<PaintableAirlockComponent, AfterAutoHandleStateEvent>(OnAirlockState);
        SubscribeLocalEvent<PaintableAirlockComponent, ComponentStartup>(OnAirlockStartup);
    }

    private void OnPainterState(EntityUid uid, SprayPainterComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (_ui.TryGetOpenUi<SprayPainterBoundUserInterface>(uid, SprayPainterUiKey.Key, out var ui))
            ui.UpdateFromComponent();
    }

    private void OnAirlockState(EntityUid uid, PaintableAirlockComponent component, ref AfterAutoHandleStateEvent args)
        => UpdateAirlockColor(uid, component);

    private void OnAirlockStartup(EntityUid uid, PaintableAirlockComponent component, ComponentStartup args)
        => UpdateAirlockColor(uid, component);

    private void UpdateAirlockColor(EntityUid uid, PaintableAirlockComponent component)
    {
        if (TryComp<SpriteComponent>(uid, out var sprite))
        {
            if (_sprites.LayerMapTryGet((uid, sprite), DoorVisualLayers.Base, out var layer, false))
                _sprites.LayerSetColor((uid, sprite), layer, component.PaintColor);
        }
    }

    protected override void CacheStyles()
    {
        base.CacheStyles();

        Entries.Clear();
        foreach (var style in Styles)
        {
            var name = style.Name;
            string? iconPath = Groups
              .FindAll(x => x.StylePaths.ContainsKey(name))?
              .MaxBy(x => x.IconPriority)?.StylePaths[name];
            if (iconPath == null)
            {
                Entries.Add(new SprayPainterEntry(name, null));
                continue;
            }

            RSIResource doorRsi = _resourceCache.GetResource<RSIResource>(SpriteSpecifierSerializer.TextureRoot / new ResPath(iconPath));
            if (!doorRsi.RSI.TryGetState("closed", out var icon))
            {
                Entries.Add(new SprayPainterEntry(name, null));
                continue;
            }

            Entries.Add(new SprayPainterEntry(name, icon.Frame0));
        }
    }
}

public sealed class SprayPainterEntry
{
    public string Name;
    public Texture? Icon;

    public SprayPainterEntry(string name, Texture? icon)
    {
        Name = name;
        Icon = icon;
    }
}
