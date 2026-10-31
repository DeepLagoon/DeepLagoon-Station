using Robust.Shared.Configuration;

namespace Content.Server.GameTicking;

/// <summary>Public round numbering is independent of immutable database identifiers.</summary>
[CVarDefs]
public sealed class RoundNumberCVars
{
    public static readonly CVarDef<int> Offset =
        CVarDef.Create("game.round_number_offset", 0, CVar.SERVERONLY);

    public static int Display(int databaseId, int offset) => Math.Max(0, databaseId - Math.Max(0, offset));
}
