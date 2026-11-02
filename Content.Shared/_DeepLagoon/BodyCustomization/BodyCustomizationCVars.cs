using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.BodyCustomization;

[CVarDefs]
public static class BodyCustomizationCVars
{
    /// <summary>
    /// Enables automatic face fitting for Vox and Resomi headwear, masks and balaclavas.
    /// Disabled temporarily; native species sprites and displacement maps are preserved.
    /// </summary>
    public static readonly CVarDef<bool> AvianMaskFit =
        CVarDef.Create("body_customization.avian_mask_fit", false, CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<bool> ChemicalChanges =
        CVarDef.Create("body_customization.chemical_changes", true, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
}
