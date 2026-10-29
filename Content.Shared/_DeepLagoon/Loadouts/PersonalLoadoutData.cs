using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Loadouts;

[Prototype]
public sealed partial class PersonalLoadoutCategoryPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public bool Root;
    [DataField] public List<ProtoId<PersonalLoadoutCategoryPrototype>> SubCategories = new();
}

/// <summary>Donor requirements use plain IDs so absent donor jobs cannot unlock restricted equipment.</summary>
[DataDefinition]
public sealed partial class PersonalLoadoutRequirement
{
    [DataField(required: true)] public string Kind = string.Empty;
    [DataField] public bool Inverted;
    [DataField] public List<string> Jobs = new();
    [DataField] public List<string> Departments = new();
    [DataField] public List<string> Species = new();
    [DataField] public List<string> Traits = new();
    [DataField] public List<string> Employers = new();
    [DataField] public List<string> Nationalities = new();
    [DataField] public List<string> Lifepaths = new();
    [DataField] public List<PersonalLoadoutRequirement> Requirements = new();
    [DataField] public string Department = string.Empty;
    [DataField] public string Tracker = string.Empty;
    [DataField] public string Sex = string.Empty;
    [DataField] public string Gender = string.Empty;
    [DataField] public float Min;
    [DataField] public float Max = float.MaxValue;
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class PersonalLoadoutCustomization : IEquatable<PersonalLoadoutCustomization>
{
    [DataField] public string? Name { get; set; }
    [DataField] public string? Description { get; set; }
    [DataField] public string? Color { get; set; }
    [DataField] public bool Heirloom { get; set; }
    [DataField] public string? Slot { get; set; }
    public bool Equals(PersonalLoadoutCustomization? other) => other != null && Name == other.Name && Description == other.Description && Color == other.Color && Heirloom == other.Heirloom && Slot == other.Slot;
    public override bool Equals(object? obj) => obj is PersonalLoadoutCustomization other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, Description, Color, Heirloom, Slot);
}
