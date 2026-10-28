using System.Linq;
using Content.Shared._DeepLagoon.Apartments;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public bool SaveAtmosphereSettings(EntityUid grid, int oxygenPercent)
    {
        if (oxygenPercent is < 0 or > 100 || !HasComp<ApartmentGridComponent>(grid))
            return false;
        var instance = _instances.Values.FirstOrDefault(value => value.Grid == grid);
        // Admin-created apartment grids have no account save.
        if (instance == null)
            return !_retiring.Any(value => value.Grid == grid);
        if (instance.Delta.OxygenPercent == oxygenPercent)
            return true;
        var previous = instance.Delta;
        var delta = new ApartmentDelta
        {
            SchemaVersion = previous.SchemaVersion, Template = previous.Template, TemplateVersion = previous.TemplateVersion,
            Revision = previous.Revision + 1, Removed = previous.Removed, Changed = previous.Changed, Added = previous.Added,
            OxygenPercent = oxygenPercent,
        };
        try
        {
            if (!Store.Save(instance.Owner.UserId, delta, previous.Revision))
                return false;
        }
        catch (Exception exception)
        {
            Log.Error($"Apartment atmosphere save failed: {exception}");
            return false;
        }
        instance.Delta = delta;
        return true;
    }
}
