using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.GameTicking;
using Content.Shared.Tag;
using Robust.Client.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._Mono.Shipyard;

/// <summary>A local, read-only plan. Ship maps and their gameplay components are never instantiated.</summary>
public sealed class ShipyardPreviewSystem : EntitySystem
{
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private TagSystem _tags = default!;
    private readonly TguiSpriteImages _images = new();
    private ShipyardPreviewWindow? _window;
    public bool IsOpen => _window != null;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => Close());
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => Close());
    }

    public override void Shutdown() { Close(); base.Shutdown(); }

    public void Open(VesselPrototype vessel)
    {
        Close();
        var window = _window = new ShipyardPreviewWindow();
        window.OnClose += Close;
        window.Panel.OnClose += Close;
        window.Panel.OnAction += (action, _) => { if (action == "close") Close(); };
        try
        {
            using var reader = _resources.ContentFileReadText(vessel.ShuttlePath);
            window.Panel.SetState("ShipyardPreview", BuildData(vessel, ShipyardPreviewScene.Read(reader)).ToString(), vessel.LocalizedName);
        }
        catch (Exception exception)
        {
            Log.Warning($"Unable to read preview for {vessel.ID}: {exception.Message}");
            window.Panel.SetState("ShipyardPreview", new TguiData().String("name", vessel.LocalizedName)
                .String("error", Loc.GetString("shipyard-preview-load-failed")).ToString(), vessel.LocalizedName);
        }
        window.OpenCentered();
    }

    public TguiData BuildData(VesselPrototype vessel, ShipyardPreviewScene scene)
    {
        var objects = scene.Items.Where(item => item.Kind == "entity" && _prototypes.HasIndex<EntityPrototype>(item.Prototype)).ToList();
        var definitions = objects.Select(item => item.Prototype).Distinct().ToDictionary(id => id, id => _prototypes.Index<EntityPrototype>(id));
        bool Wall(EntityPrototype proto) => proto.TryGetComponent<TagComponent>(out var tags) && (_tags.HasTag(tags, "Wall") || _tags.HasTag(tags, "Window"));
        bool Door(EntityPrototype proto) => proto.TryGetComponent<Content.Shared.Pinpointer.NavMapDoorComponent>(out _);
        var structures = objects.Where(item => Wall(definitions[item.Prototype]) || Door(definitions[item.Prototype])).ToList();
        var walls = new TguiWallImages(_images, objects, _prototypes);
        var equipment = objects.Except(structures).Concat(scene.Items.Where(item => item.Kind == "decal")).ToList();
        return new TguiData().String("token", vessel.ID).String("name", vessel.LocalizedName).String("description", vessel.Description)
            .Number("x", scene.X).Number("y", scene.Y).Number("width", scene.Width).Number("height", scene.Height)
            .Array("tiles", scene.Items.Where(item => item.Kind == "tile").Select(item => new TguiData().Number("x", item.X).Number("y", item.Y)))
            .Array("structures", structures.Select(item => new TguiData().Number("x", item.X).Number("y", item.Y)
                .Number("rotation", item.Rotation).Bool("door", Door(definitions[item.Prototype]))
                .Bool("noRotation", definitions[item.Prototype].TryGetComponent<SpriteComponent>(out var sprite) && sprite.NoRotation)
                .Array("images", Wall(definitions[item.Prototype]) ? walls.Images(item) : Array.Empty<TguiData>())))
            .Array("items", equipment.Select(item => new TguiData().String("id", item.Id).String("kind", item.Kind)
                .String("prototype", item.Prototype).Number("x", item.X).Number("y", item.Y).Number("rotation", item.Rotation)
                .String("color", item.Color).Number("z", item.Z)))
            .Array("visuals", equipment.Select(item => (item.Kind, item.Prototype)).Distinct().Select(key =>
            {
                if (key.Kind == "decal")
                    return new TguiData().String("kind", key.Kind).String("id", key.Prototype)
                        .Array("images", _prototypes.TryIndex<Content.Shared.Decals.DecalPrototype>(key.Prototype, out var decal)
                            ? new[] { new TguiData().String("url", _images.Frame(decal.Sprite)) } : Array.Empty<TguiData>());
                var proto = definitions[key.Prototype];
                proto.TryGetComponent<SpriteComponent>(out var sprite);
                return new TguiData().String("kind", key.Kind).String("id", key.Prototype)
                    .Bool("noRotation", sprite?.NoRotation ?? false).Number("drawDepth", sprite?.DrawDepth ?? 0)
                    .Array("images", _images.MapItem(key.Prototype));
            }));
    }

    public void Close()
    {
        var window = _window;
        _window = null;
        _images.Reset();
        if (window == null) return;
        window.OnClose -= Close;
        window.Panel.OnClose -= Close;
        window.Close();
        window.Dispose();
    }
}

public sealed class ShipyardPreviewWindow : FancyWindow
{
    public readonly TguiPanel Panel = new(forceTgui: true);
    public ShipyardPreviewWindow()
    {
        Title = Loc.GetString("shipyard-preview-ui-title");
        SetSize = new Vector2(1000, 720);
        MinSize = new Vector2(560, 400);
        ContentsContainer.AddChild(Panel);
    }
}
