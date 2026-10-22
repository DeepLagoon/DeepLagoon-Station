using Content.Shared._DeepLagoon.InteractionPanel;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalOverlay : Overlay
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly ShaderInstance _shader;
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public InteractionPanelArousalOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _prototypes.Index<ShaderPrototype>(new ProtoId<ShaderPrototype>("GradientCircleMask")).InstanceUnique();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        // Только текущий управляемый моб. В превью, чужих камерах и после выхода из тела эффекта нет.
        if (!_entities.TryGetComponent(_players.LocalEntity, out EyeComponent? eye) || args.Viewport.Eye != eye.Eye) return;
        var fraction = _entities.TryGetComponent(_players.LocalEntity, out InteractionPanelArousalComponent? arousal) && arousal.Maximum > 0
            ? Math.Clamp(arousal.Current / arousal.Maximum, 0, 1) : 0;
        if (_entities.TryGetComponent(_players.LocalEntity, out Content.Shared._DeepLagoon.LustWish.LustWishWornEffectsComponent? worn) && worn.Focused)
            fraction = Math.Max(fraction, 0.45f);
        if (fraction <= 0) return;
        var settings = _prototypes.Index<InteractionPanelArousalPrototype>(new ProtoId<InteractionPanelArousalPrototype>("InteractionPanelArousal"));
        var width = args.ViewportBounds.Width;
        var color = settings.VignetteColor;
        var alpha = float.IsFinite(settings.VignetteMaxAlpha) ? Math.Clamp(settings.VignetteMaxAlpha, 0, 1) : 0.65f;
        _shader.SetParameter("color", new Vector3(color.R, color.G, color.B));
        _shader.SetParameter("time", 0f);
        _shader.SetParameter("darknessAlphaOuter", alpha * color.A * fraction);
        _shader.SetParameter("innerCircleRadius", width * (0.4f - 0.2f * fraction));
        _shader.SetParameter("innerCircleMaxRadius", width * 0.4f);
        _shader.SetParameter("outerCircleRadius", width * 0.65f);
        _shader.SetParameter("outerCircleMaxRadius", width * 0.65f);
        args.WorldHandle.UseShader(_shader);
        args.WorldHandle.DrawRect(args.WorldAABB, Color.White);
        args.WorldHandle.UseShader(null);
    }

    protected override void DisposeBehavior()
    {
        _shader.Dispose();
        base.DisposeBehavior();
    }
}

public sealed partial class InteractionPanelArousalVisualSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    private InteractionPanelArousalOverlay? _overlay;
    public override void Initialize()
    {
        _overlay = new InteractionPanelArousalOverlay();
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        if (_overlay != null) { _overlays.RemoveOverlay(_overlay); _overlay.Dispose(); _overlay = null; }
        base.Shutdown();
    }
}
