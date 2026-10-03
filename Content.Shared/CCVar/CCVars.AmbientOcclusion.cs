using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> AmbientOcclusionSilhouettes =
        CVarDef.Create("graphics.ambient_occlusion_silhouettes", true, CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<int> AmbientOcclusionSilhouetteScale =
        CVarDef.Create("graphics.ambient_occlusion_silhouette_scale", 118, CVar.CLIENTONLY | CVar.ARCHIVE);
    public static readonly CVarDef<bool> AmbientOcclusionEnabled =
        CVarDef.Create("graphics.ambient_occlusion", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int> AmbientOcclusionIntensity =
        CVarDef.Create("graphics.ambient_occlusion_intensity", 300, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> AmbientOcclusionEntities =
        CVarDef.Create("graphics.ambient_occlusion_entities", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}
