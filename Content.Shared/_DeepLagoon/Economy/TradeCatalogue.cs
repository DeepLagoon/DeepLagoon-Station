using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Economy;

public static class TradeCatalogue
{
    private static readonly string[] Commodities = ["SheetSteel", "SheetGlass", "SheetRGlass", "SheetPlasteel", "SheetPlastic", "SheetPlasma", "SheetUranium",
        "MaterialGold", "MaterialSilver", "MaterialCloth", "MaterialDurathread", "MaterialDiamond", "MaterialWoodPlank", "MaterialBananium"];
    public static string? Commodity(string id)
    {
        foreach (var type in Commodities)
            if (id == type || id == type + "1" || id == type + "10" || id == type + "30") return type;
        return null;
    }
    public static string GroupKey(string prototype, bool buy) => (buy ? "buy:" : "sale:") + (Commodity(prototype) ?? prototype);
    public static readonly string[] Categories = ["", "materials", "equipment", "weapons", "food", "clothing", "other"];
    public static bool SupportsBattery(EntityPrototype prototype) => prototype.Components.ContainsKey("Battery") || prototype.Components.ContainsKey("PowerCellSlot");

    public static string Category(EntityPrototype prototype)
    {
        var components = prototype.Components;
        if (components.ContainsKey("Gun") || components.ContainsKey("Ammo") || components.ContainsKey("BallisticAmmoProvider") ||
            components.ContainsKey("CartridgeAmmo") || components.ContainsKey("MagazineAmmoProvider")) return "weapons";
        if (components.ContainsKey("Food") || components.ContainsKey("Drink")) return "food";
        if (components.ContainsKey("Clothing")) return "clothing";
        if (components.ContainsKey("Stack")) return "materials";
        if (components.ContainsKey("Machine") || components.ContainsKey("Flatpack") || components.ContainsKey("Tool") ||
            components.ContainsKey("ResearchDisk") || components.ContainsKey("ComputerBoard")) return "equipment";
        return "other";
    }
}
