using System.Linq;
using Content.Shared.CCVar;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;

namespace Content.Shared.Traits;

/// <summary>Shared, data-driven requirements used by both the editor and the server.</summary>
[DataDefinition]
public sealed partial class TraitRequirement
{
    [DataField] public TraitRequirementKind Kind;
    [DataField] public bool Inverted;
    [DataField] public string GroupName = "trait-group-caster";
    [DataField] public List<string> Values = new();
    [DataField] public List<TraitRequirement> Requirements = new();
    [DataField] public float Min = float.MinValue;
    [DataField] public float Max = float.MaxValue;

    public bool IsValid(HumanoidCharacterProfile profile, IReadOnlySet<ProtoId<TraitPrototype>> selection,
        IPrototypeManager prototypes, IConfigurationManager config, ProtoId<JobPrototype>? job,
        string traitId, out string reason)
    {
        var species = prototypes.Index(profile.Species);
        var result = Kind switch
        {
            TraitRequirementKind.Species => Values.Contains(profile.Species.Id),
            TraitRequirementKind.Job => job != null && Values.Contains(job.Value.Id),
            TraitRequirementKind.Department => job != null && Values.Any(id =>
                prototypes.TryIndex<DepartmentPrototype>(id, out var department) && department.Roles.Contains(job.Value)),
            TraitRequirementKind.Trait => Values.Any(id => selection.Contains(id)),
            TraitRequirementKind.Any => Requirements.Any(r => r.IsValid(profile, selection, prototypes, config, job, traitId, out _)),
            TraitRequirementKind.Group => Values.Count(id => id != traitId && selection.Contains(id)) < Max,
            TraitRequirementKind.Height => InRange(profile.Appearance.Height * species.AverageHeight),
            TraitRequirementKind.Width => InRange(profile.Appearance.Width * species.AverageWidth),
            TraitRequirementKind.Weight => InRange(GetWeight(profile, species, prototypes)),
            TraitRequirementKind.RestrictedGear => config.GetCVar(CCVars.RestrictedGearEnabled),
            _ => false,
        };
        var names = Values.Select(id => Kind switch
        {
            TraitRequirementKind.Species when prototypes.TryIndex<SpeciesPrototype>(id, out var s) => Loc.GetString(s.Name),
            TraitRequirementKind.Job when prototypes.TryIndex<JobPrototype>(id, out var j) => Loc.GetString(j.Name),
            TraitRequirementKind.Department when prototypes.TryIndex<DepartmentPrototype>(id, out var d) => Loc.GetString(d.Name),
            TraitRequirementKind.Trait when prototypes.TryIndex<TraitPrototype>(id, out var t) => Loc.GetString(t.Name),
            _ => id,
        });
        reason = Kind == TraitRequirementKind.Any
            ? string.Join(" / ", Requirements.Select(r => { r.IsValid(profile, selection, prototypes, config, job, traitId, out var text); return text; }))
            : Loc.GetString("trait-requirement-" + Kind.ToString().ToLowerInvariant(),
                ("restriction", Loc.GetString(Inverted ? "trait-requirement-excludes" : "trait-requirement-needs")),
                ("group", Loc.GetString(GroupName)), ("values", string.Join(", ", names)),  ("min", Min == float.MinValue ? 0 : Min), ("max", Max));
        return Inverted ? !result : result;
    }

    private bool InRange(float value) => value >= Min && value <= Max;

    private static float GetWeight(HumanoidCharacterProfile profile, SpeciesPrototype species, IPrototypeManager prototypes)
    {
        if (!prototypes.Index(species.Prototype).TryGetComponent<FixturesComponent>(out var fixtures)
            || !fixtures.Fixtures.TryGetValue("fix1", out var fixture))
            return float.PositiveInfinity;
        var scale = (profile.Appearance.Height + profile.Appearance.Width) / 2f;
        return MathF.Round(MathF.PI * MathF.Pow(fixture.Shape.Radius * scale, 2) * fixture.Density);
    }
}

public enum TraitRequirementKind : byte
{
    Species, Job, Department, Trait, Any, Group, Height, Width, Weight, RestrictedGear,
}
