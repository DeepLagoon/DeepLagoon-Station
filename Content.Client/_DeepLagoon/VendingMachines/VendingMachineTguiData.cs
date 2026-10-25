using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.VendingMachines;
using Content.Shared._NF.Bank;
using Content.Shared.VendingMachines;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.VendingMachines;

/// <summary>Prototype catalogue only: previews must not start arbitrary entities and their gameplay systems.</summary>
public sealed class VendingMachineTguiData(IEntityManager entities)
{
    private readonly IPrototypeManager _prototypes = IoCManager.Resolve<IPrototypeManager>();
    private static TguiSpriteImages _images => TguiSpriteImages.Shared;

    public static string Key(VendingMachineInventoryEntry entry) => $"{(int) entry.Type}:{entry.ID}";

    public void Reset() => _images.Reset();

    public string BuildStatic(IEnumerable<VendingMachineInventoryEntry> inventory, float modifier, bool requiresCash)
    {
        var items = new List<TguiData>();
        foreach (var entry in inventory)
        {
            if (!_prototypes.TryIndex<EntityPrototype>(entry.ID, out var prototype))
                continue;

            var price = requiresCash ? VendingMachinePricing.GetPrice(prototype, modifier, _prototypes, entities) : 0;
            items.Add(new TguiData().String("key", Key(entry)).String("id", entry.ID).Number("type", (int) entry.Type)
                .String("name", prototype.Name).String("description", prototype.Description)
                .Number("price", price).String("priceText", price == 0 ? Loc.GetString("dl-vending-free") : BankSystemExtensions.ToSpesoString(price))
                .String("code", SlotCode(items.Count)));
        }

        var labels = new TguiData();
        foreach (var key in new[] { "subtitle", "catalogue", "preview", "balance", "no-account", "cash", "free", "buy",
                     "unavailable", "remaining", "unlimited", "empty", "no-results", "select", "chute", "off", "broken",
                     "busy", "denied", "no-image", "payment", "search" })
            labels.String(key, Loc.GetString("dl-vending-" + key));

        return new TguiData().Array("items", items).Object("labels", labels).ToString();
    }

    // Three positions per shelf; extend past Z for machines with unusually large catalogues.
    private static string SlotCode(int index)
    {
        var row = index / 3 + 1;
        var letters = "";
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        while (row > 0)
        {
            row--;
            // Keep both concatenation operands as strings; Roslyn's char fast path uses forbidden spans.
            letters = alphabet.Substring(row % 26, 1) + letters;
            row /= 26;
        }
        return letters + (index % 3 + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static IEnumerable<TguiData> Stock(IEnumerable<VendingMachineInventoryEntry> inventory) => inventory.Select(entry =>
        new TguiData().String("key", Key(entry)).Bool("available", entry.Amount > 0)
            .Bool("unlimited", entry.Amount == uint.MaxValue).String("amount", entry.Amount.ToString(CultureInfo.InvariantCulture)));

    public string BuildIcons(IEnumerable<string> ids) => "{\"data\":" + new TguiData().Object("iconBatch", new TguiData()
        .Array("icons", ids.Where(id => _prototypes.HasIndex<EntityPrototype>(id)).Select(id =>
            new TguiData().String("id", id).Array("layers", _images.Item(id))))) + "}";
}
