using Robust.Shared.Configuration;

namespace Content.Shared._Scp.ScpCCVars;

[CVarDefs]
public sealed partial class ScpCCVars
{
    /**
     * Shader
     */

    /// <summary>
    /// Выключен ли шейдер зернистости? // Shitty translation, dont take as fact - "Is the grain shader disabled?"
    /// </summary>
    public static readonly CVarDef<bool> GrainToggleOverlay =
        CVarDef.Create("shader.grain_toggle_overlay", false, CVar.CLIENTONLY | CVar.ARCHIVE); // Mono - false by default

    /// <summary>
    /// Сила шейдера зернистости // Shitty translation, dont take as fact - "The power of the grain shader"
    /// </summary>
    public static readonly CVarDef<int> GrainStrength =
        CVarDef.Create("shader.grain_strength", 140, CVar.CLIENTONLY | CVar.ARCHIVE);

}
