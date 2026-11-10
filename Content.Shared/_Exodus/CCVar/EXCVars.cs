using Robust.Shared.Configuration;

namespace Content.Shared._Exodus.CCVar;

[CVarDefs]
public static partial class EXCVars
{
    public static readonly CVarDef<bool> ParallelProjectileRaycasts =
        CVarDef.Create("exodus.parallel_projectile_raycasts", true, CVar.SERVERONLY);

    public static readonly CVarDef<bool> ParallelMoverUpdate =
        CVarDef.Create("exodus.parallel_mover_update", true, CVar.SERVERONLY);

    public static readonly CVarDef<string> DecalFavoriteColors =
        CVarDef.Create("exodus.decal_favorite_colors", "", CVar.CLIENTONLY | CVar.ARCHIVE);

}
