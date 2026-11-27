using System.Linq;
using Robust.Shared.Prototypes;
namespace Content.Shared._DeepLagoon.Mining;

/// <summary>Retained for admin use and legacy deserialization, unavailable through player acquisition.</summary>
public static class LaserDrillAvailability
{
    public static bool IsRetiredPrototype(IPrototypeManager prototypes, string id) =>
        id is "LaserDrillBase" or "LaserDrill" or "StationLaserDrill" or "LaserDrillFlatpack" ||
        prototypes.HasIndex<EntityPrototype>(id) && prototypes.EnumerateAllParents<EntityPrototype>(id)
            .Any(p => p.id is "LaserDrillBase" or "LaserDrillFlatpack");
}
