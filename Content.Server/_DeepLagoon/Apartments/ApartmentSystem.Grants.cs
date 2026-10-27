using Robust.Shared.Network;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public string? GrantTemplate(NetUserId owner, string templateId, out bool alreadyOwned)
    {
        alreadyOwned = false;
        if (!Enabled) return "apartments_disabled";
        if (!_prototypes.TryIndex<ApartmentTemplatePrototype>(templateId, out var template) || !Available(template))
            return "apartment_unavailable";
        alreadyOwned = Store.Owned(owner.UserId, template.ID);
        if (alreadyOwned) return null;
        try
        {
            EnsureMaps();
            var validation = CreateFresh(owner, template.ID, true);
            Del(validation.Grid);
        }
        catch (Exception e)
        {
            Log.Warning($"Apartment grant validation failed: template={template.ID} error={e.GetType().Name}");
            return "apartment_invalid_map";
        }
        Store.BeginPurchase(owner.UserId, template.ID, 0);
        Store.Grant(owner.UserId, template.ID);
        return null;
    }
}
