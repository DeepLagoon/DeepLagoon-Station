using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mantis;

namespace Content.Client._DeepLagoon.Mantis;

public static class PsiSpectrometerTguiData
{
    public static TguiData Build(PsiSpectrometerState state)
    {
        var signatures = new List<TguiData>();

        foreach (var signature in state.Signatures)
        {
            signatures.Add(new TguiData()
                .String("type", Type(signature.Type))
                .String("sector", Sector(signature.Sector))
                .String("name", signature.Name)
                .Number("intensity", (int) (signature.Intensity * 100)));
        }

        return new TguiData()
            .Object("labels", Labels())
            .Number("glimmer", state.Glimmer)
            .Number("range", MathF.Round(state.Range))
            .Bool("hunter", state.HunterMode)
            .Bool("map", state.MapOverlay)
            .Bool("ready", state.Ready)
            .Array("signatures", signatures);
    }

    public static TguiData Labels() => new TguiData()
        .String("title", Loc.GetString("psi-spectrometer-title"))
        .String("scan", Loc.GetString("psi-spectrometer-scan"))
        .String("hunter", Loc.GetString("psi-spectrometer-hunter"))
        .String("map", Loc.GetString("psi-spectrometer-map"))
        .String("ready", Loc.GetString("psi-spectrometer-ready"))
        .String("recharging", Loc.GetString("psi-spectrometer-recharging"))
        .String("empty", Loc.GetString("psi-spectrometer-empty"))
        .String("glimmer", Loc.GetString("psi-spectrometer-glimmer-label"))
        .String("range", Loc.GetString("psi-spectrometer-range-label"))
        .String("signature", Loc.GetString("psi-spectrometer-column-signature"))
        .String("sector", Loc.GetString("psi-spectrometer-column-sector"))
        .String("subject", Loc.GetString("psi-spectrometer-column-subject"))
        .String("intensity", Loc.GetString("psi-spectrometer-column-intensity"));

    private static string Type(NoosphericSignatureType type) =>
        Loc.GetString("psi-spectrometer-type-" + type.ToString().ToLowerInvariant());

    private static string Sector(NoosphericSector sector) =>
        Loc.GetString("psi-spectrometer-sector-" + sector.ToString().ToLowerInvariant());
}
