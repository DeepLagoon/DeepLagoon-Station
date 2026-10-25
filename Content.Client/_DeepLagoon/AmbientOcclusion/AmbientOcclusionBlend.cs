using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>Maximum contact opacity using stock blend modes on a linear UNORM target with alpha one.</summary>
internal sealed class AmbientOcclusionBlend(IPrototypeManager prototypes)
{
    private static readonly ProtoId<ShaderPrototype> SubtractShader = "AmbientOcclusionSubtract";
    private static readonly ProtoId<ShaderPrototype> AddShader = "AmbientOcclusionAdd";
    private readonly ShaderInstance _subtract = prototypes.Index(SubtractShader).Instance();
    private readonly ShaderInstance _add = prototypes.Index(AddShader).Instance();

    public void DrawTexture(DrawingHandleWorld handle, Texture texture, Box2 quad, Color tint, UIBox2? source = null)
    {
        // Saturating subtraction followed by addition gives max(previous, opacity).
        // Keep each pair adjacent: combining whole scene passes would instead sum overlapping contacts.
        handle.UseShader(_subtract);
        handle.DrawTextureRectRegion(texture, quad, tint, source);
        handle.UseShader(_add);
        handle.DrawTextureRectRegion(texture, quad, tint, source);
    }

    public void DrawRect(DrawingHandleWorld handle, Box2 quad, Color tint)
    {
        handle.UseShader(_subtract);
        handle.DrawRect(quad, tint);
        handle.UseShader(_add);
        handle.DrawRect(quad, tint);
    }
}
