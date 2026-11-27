using Robust.Shared.Configuration;

namespace Content.Server._DeepLagoon.Cleanup;

[CVarDefs]
public sealed class AgeCleanupCVars
{
    public static readonly CVarDef<float> OpenedMailSeconds = CVarDef.Create("cleanup.opened_mail_seconds", 300f, CVar.SERVERONLY);
}
