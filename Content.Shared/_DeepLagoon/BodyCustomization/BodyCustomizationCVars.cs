using Robust.Shared.Configuration;

namespace Content.Shared._DeepLagoon.BodyCustomization;

[CVarDefs]
public static class BodyCustomizationCVars
{
    public static readonly CVarDef<bool> ChemicalChanges =
        CVarDef.Create("body_customization.chemical_changes", true, CVar.CLIENT | CVar.ARCHIVE | CVar.REPLICATED);
}
