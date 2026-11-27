using System.Numerics;
using Content.Client.Resources;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Hands;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Client._DeepLagoon.Mantis;

public sealed class PsiSpectrometerOverlay : Overlay
{
    private readonly PsiSpectrometerOverlaySystem _source;
    private readonly List<Contact> _contacts = new();

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    private const float MarkerRadius = 0.3f;

    private static readonly Color SapientColor = Color.FromHex("#6fd3ff");
    private static readonly Color KineticColor = Color.FromHex("#ffd166");
    private static readonly Color AnomalousColor = Color.FromHex("#c77dff");

    public readonly record struct Contact(Vector2 Position, string Name, Color Color, float Intensity);

    public PsiSpectrometerOverlay(PsiSpectrometerOverlaySystem source)
    {
        _source = source;
    }

    public static Color ColorFor(NoosphericSignatureType type) => type switch
    {
        NoosphericSignatureType.Kinetic => KineticColor,
        NoosphericSignatureType.Anomalous => AnomalousColor,
        _ => SapientColor,
    };

    protected override void Draw(in OverlayDrawArgs args)
    {
        _contacts.Clear();
        _source.Collect(args.MapId, _contacts);

        if (_contacts.Count == 0)
            return;

        var handle = args.WorldHandle;
        foreach (var contact in _contacts)
        {
            var radius = MarkerRadius * (0.7f + contact.Intensity * 0.8f);
            handle.DrawCircle(contact.Position, radius * 2.1f, contact.Color.WithAlpha(0.22f));
            handle.DrawCircle(contact.Position, radius, contact.Color);
            handle.DrawCircle(contact.Position, radius, contact.Color, filled: false);
        }
    }
}

public sealed class PsiSpectrometerLabelOverlay : Overlay
{
    private readonly PsiSpectrometerOverlaySystem _source;
    private readonly Font _font;
    private readonly List<PsiSpectrometerOverlay.Contact> _contacts = new();

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private const float LabelScale = 0.9f;

    public PsiSpectrometerLabelOverlay(PsiSpectrometerOverlaySystem source)
    {
        _source = source;
        _font = IoCManager.Resolve<IResourceCache>().GetFont("/Fonts/NotoSans/NotoSans-Regular.ttf", 11);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.ViewportControl is not { } viewport)
            return;

        _contacts.Clear();
        _source.Collect(args.MapId, _contacts);
        if (_contacts.Count == 0)
            return;

        var handle = args.ScreenHandle;
        foreach (var contact in _contacts)
        {
            var screen = viewport.WorldToScreen(contact.Position);
            handle.DrawString(_font, screen + new Vector2(6, -6), contact.Name, LabelScale, contact.Color);
        }
    }
}

public sealed partial class PsiSpectrometerOverlaySystem : EntitySystem
{
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IOverlayManager _overlay = default!;
    [Dependency] private SharedTransformSystem _xform = default!;

    private PsiSpectrometerOverlay _world = default!;
    private PsiSpectrometerLabelOverlay _labels = default!;
    private bool _attached;

    public override void Initialize()
    {
        base.Initialize();

        _world = new PsiSpectrometerOverlay(this);
        _labels = new PsiSpectrometerLabelOverlay(this);

        SubscribeLocalEvent<PsiSpectrometerComponent, AfterAutoHandleStateEvent>(OnSpectrometerState);
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<PsiSpectrometerComponent, GotEquippedHandEvent>(OnHandChanged);
        SubscribeLocalEvent<PsiSpectrometerComponent, GotUnequippedHandEvent>(OnHandChanged);
    }

    private void OnSpectrometerState(Entity<PsiSpectrometerComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        Refresh();
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent args) => Refresh();
    private void OnPlayerDetached(LocalPlayerDetachedEvent args) => Refresh();

    private void OnHandChanged(Entity<PsiSpectrometerComponent> ent, ref GotEquippedHandEvent args) => Refresh();
    private void OnHandChanged(Entity<PsiSpectrometerComponent> ent, ref GotUnequippedHandEvent args) => Refresh();

    private void Refresh()
    {
        var active = FindActiveSpectrometer() != null;

        if (active == _attached)
            return;

        _attached = active;

        if (active)
        {
            _overlay.AddOverlay(_world);
            _overlay.AddOverlay(_labels);
        }
        else
        {
            _overlay.RemoveOverlay(_world);
            _overlay.RemoveOverlay(_labels);
        }
    }

    public void Collect(MapId mapId, List<PsiSpectrometerOverlay.Contact> into)
    {
        var component = FindActiveSpectrometer();
        if (component == null || component.LastSignatures.Count == 0)
            return;

        var owner = component.Owner;
        if (mapId != _xform.GetMapId(owner))
            return;

        foreach (var signature in component.LastSignatures)
        {
            into.Add(new PsiSpectrometerOverlay.Contact(signature.Position, signature.Name,
                PsiSpectrometerOverlay.ColorFor(signature.Type), signature.Intensity));
        }
    }

    private PsiSpectrometerComponent? FindActiveSpectrometer()
    {
        if (_player.LocalEntity is not { } local)
            return null;

        var held = EntityQueryEnumerator<PsiSpectrometerComponent>();
        while (held.MoveNext(out var uid, out var component))
        {
            if (component.MapOverlay && Transform(uid).ParentUid == local)
                return component;
        }

        var ui = EntityManager.System<SharedUserInterfaceSystem>();
        var open = EntityQueryEnumerator<PsiSpectrometerComponent>();
        while (open.MoveNext(out var uid, out var component))
        {
            if (component.MapOverlay && ui.IsUiOpen(uid, PsiSpectrometerUiKey.Key, local))
                return component;
        }

        return null;
    }
}
