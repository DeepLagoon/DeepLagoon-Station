using Content.Client.Rendering;
using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client._Scp.Shaders.Bloom;

public sealed class PointLightingOverlay : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpaceEntities;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;
    private readonly TransformSystem _transform;

    private readonly Texture _pointTexture;
    private readonly Vector2 _pointOffset;

    public List<(EntityUid uid, TransformComponent xform, Color color)> Entities = [];
    public bool Enabled;
    public float Strength = 0.5f;

    public PointLightingOverlay(IPrototypeManager prototypeManager, SpriteSystem spriteSystem, ProtoId<ShaderPrototype> shader, TransformSystem transform)
    {
        _transform = transform;
        _shader = prototypeManager.Index(shader).InstanceUnique();
        ZIndex = (int) DrawDepth.Effects + 1;

        _pointTexture = spriteSystem.Frame0(BloomOverlayVisualsComponent.Point);

        var xOffset = BloomOverlayVisualsComponent.PointOffset.X - (_pointTexture.Width / 2f) / EyeManager.PixelsPerMeter;
        var yOffset = BloomOverlayVisualsComponent.PointOffset.Y - (_pointTexture.Height / 2f) / EyeManager.PixelsPerMeter;
        _pointOffset = new Vector2(xOffset, yOffset);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        base.BeforeDraw(in args);

        return Enabled;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        var handle = args.WorldHandle;
        var bounds = args.WorldAABB.Enlarged(5f);

        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _shader.SetParameter("base_haze", BloomOverlayVisualsComponent.DefaultPointBaseHaze);
        _shader.SetParameter("hueta_divisor", BloomOverlayVisualsComponent.DefaultPointHuetaDivisor / Strength);
        handle.UseShader(_shader);

        foreach (var (uid, xform, color) in Entities)
        {
            if (xform.MapID != args.MapId)
                continue;

            var (worldPos, _, matrix) = _transform.GetRenderWorldPositionRotationMatrix((uid, xform));
            if (!bounds.Contains(worldPos))
                continue;

            handle.SetTransform(matrix);
            handle.DrawTexture(_pointTexture, _pointOffset, color);
        }

        handle.UseShader(null);
        handle.SetTransform(Matrix3x2.Identity);
    }
}
