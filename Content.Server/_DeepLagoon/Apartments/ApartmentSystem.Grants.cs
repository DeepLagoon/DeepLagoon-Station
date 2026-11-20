using System.Threading.Tasks;
using Robust.Shared.Network;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public string? GrantTemplate(NetUserId owner, string templateId, out bool alreadyOwned)
    {
        var result = WaitPersistence(GrantTemplateAsync(owner, templateId));
        alreadyOwned = result.AlreadyOwned; return result.Error;
    }
    public async Task<(string? Error, bool AlreadyOwned)> GrantTemplateAsync(NetUserId owner, string templateId)
    {
        if (!Enabled) return ("apartments_disabled", false);
        if (!_prototypes.TryIndex<ApartmentTemplatePrototype>(templateId, out var template) || !Available(template))
            return ("apartment_unavailable", false);
        var alreadyOwned = OwnsTemplate(owner, template);
        if (alreadyOwned) return (null, true);
        if (template.ExistingOnly) return ("apartment_unavailable", false);
        try
        {
            EnsureMaps();
            var validation = CreateFresh(owner, template.ID, true);
            Del(validation.Grid);
        }
        catch (Exception e)
        {
            Log.Warning($"Apartment grant validation failed: template={template.ID} error={e.GetType().Name}");
            return ("apartment_invalid_map", false);
        }
        await Store.BeginPurchaseAsync(owner.UserId, template.ID, 0);
        await Store.GrantAsync(owner.UserId, template.ID);
        return (null, false);
    }
}
