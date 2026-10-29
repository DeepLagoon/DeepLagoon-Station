using System.Linq;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Shared.Traits;

public static class TraitSelection
{
    public static ProtoId<JobPrototype>? PreferredJob(HumanoidCharacterProfile profile) => profile.JobPriorities
        .Where(p => p.Value != JobPriority.Never).OrderByDescending(p => p.Value)
        .ThenBy(p => p.Key.Id, StringComparer.Ordinal).Select(p => (ProtoId<JobPrototype>?) p.Key).FirstOrDefault();

    public static bool IsAvailable(TraitPrototype trait, HumanoidCharacterProfile profile,
        IReadOnlySet<ProtoId<TraitPrototype>> selection, IPrototypeManager prototypes,
        IConfigurationManager config, ProtoId<JobPrototype>? job, out string reason)
    {
        reason = "";
        if (trait.Disabled || !config.GetCVar(CCVars.GameTraitsEnabled))
            reason = Loc.GetString("trait-unavailable");
        else if (trait.SpeciesBlacklist.Contains(profile.Species))
            reason = Loc.GetString("trait-species-unavailable");
        else if (selection.Any(id => id != trait.ID && prototypes.TryIndex(id, out var other)
                     && (trait.MutuallyExclusiveTraits.Contains(id) || other.MutuallyExclusiveTraits.Contains(trait.ID))))
            reason = Loc.GetString("trait-conflict");
        else
        {
            foreach (var requirement in trait.Requirements)
                if (!requirement.IsValid(profile, selection, prototypes, config, job, trait.ID, out reason))
                    return false;
            reason = "";
        }
        return reason.Length == 0;
    }

    public static TraitSelectionStatus Evaluate(HumanoidCharacterProfile profile, IPrototypeManager prototypes,
        IConfigurationManager config, ProtoId<JobPrototype>? job = null)
    {
        long points = config.GetCVar(CCVars.GameTraitsDefaultPoints);
        if (prototypes.TryIndex(profile.Species, out var species)) points += species.BonusTraitPoints;
        long slots = 0;
        var reasons = new List<string>();
        foreach (var id in profile.TraitPreferences.OrderBy(id => id.Id, StringComparer.Ordinal))
        {
            if (!prototypes.TryIndex(id, out var trait))
            {
                reasons.Add(Loc.GetString("trait-unknown", ("id", id.Id)));
                continue;
            }
            if (!IsAvailable(trait, profile, profile.TraitPreferences, prototypes, config, job, out var reason))
                reasons.Add($"{Loc.GetString(trait.Name)}: {reason}");
            points += trait.Points;
            slots += trait.Slots;
            if (trait.Slots < 0) reasons.Add(Loc.GetString("trait-unavailable"));
        }
        var maxSlots = Math.Max(0, config.GetCVar(CCVars.GameTraitsMax));
        if (points < 0) reasons.Add(Loc.GetString("trait-points-exceeded"));
        if (slots > maxSlots) reasons.Add(Loc.GetString("trait-slots-exceeded"));
        return new(points, slots, maxSlots, reasons);
    }
}

public sealed record TraitSelectionStatus(long Points, long Slots, int MaxSlots, List<string> Reasons)
{
    public bool Valid => Reasons.Count == 0;
}
