using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.Art;

[CVarDefs]
public sealed class ArtCVars
{
    /// <summary>Public HTTPS URL of this server's /art-tablet/ status-host route. Empty disables browser handoff.</summary>
    public static readonly CVarDef<string> TabletUrl = CVarDef.Create("art.tablet_url", "", CVar.SERVERONLY);
}
