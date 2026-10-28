using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> LightBloomEnabled =
        CVarDef.Create("graphics.light_bloom", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int> LightBloomIntensity =
        CVarDef.Create("graphics.light_bloom_intensity", 50, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int> LightBloomMaxLights =
        CVarDef.Create("graphics.light_bloom_max_lights", 64, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> AmbientOcclusion =
        CVarDef.Create("light.ambient_occlusion", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<string> AmbientOcclusionColor =
        CVarDef.Create("light.ambient_occlusion_color", "#04080FAA", CVar.CLIENTONLY);

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<float> AmbientOcclusionDistance =
        CVarDef.Create("light.ambient_occlusion_distance", 4f, CVar.CLIENTONLY);
}
