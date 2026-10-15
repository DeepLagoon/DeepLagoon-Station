using System.Linq;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.CCVar;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Loadouts;

/// <summary>Validate on both client and server; the server always supplies the actually assigned job.</summary>
public sealed partial class PersonalLoadoutSystem : EntitySystem
{
    public const string Role = "DLEinsteinPersonal";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ISharedPlaytimeManager _playtime = default!;

    public int Points => Math.Max(0, _configuration.GetCVar(CCVars.PersonalLoadoutPoints));
    public int GetPoints(ICommonSession? session) => Points + EntityManager.System<SharedDiscordBoostSystem>().GetLoadoutPointBonus(session);

    public IEnumerable<Loadout> GetSelections(HumanoidCharacterProfile profile) => profile.Loadouts.TryGetValue(Role, out var role)
        ? role.SelectedLoadouts.Values.SelectMany(x => x)
        : Enumerable.Empty<Loadout>();

    public bool CanUse(LoadoutPrototype prototype, HumanoidCharacterProfile profile, string job, ICommonSession? session, out string reason)
    {
        reason = string.Empty;
        if (!_configuration.GetCVar(CCVars.PersonalLoadoutsEnabled))
        {
            reason = Loc.GetString("dl-loadout-disabled");
            return false;
        }
        if (!CanUseDonor(prototype, session, out reason))
            return false;
        var times = session == null ? new Dictionary<string, TimeSpan>() : _playtime.GetPlayTimes(session);
        foreach (var requirement in prototype.PersonalRequirements)
        {
            if (!Check(requirement, profile, job, times, session))
            {
                reason = Loc.GetString("dl-loadout-requirement", ("requirement", Describe(requirement)));
                return false;
            }
        }
        return true;
    }

    public bool CanUseDonor(LoadoutPrototype prototype, ICommonSession? session, out string reason)
    {
        reason = string.Empty;
        if (EntityManager.System<SharedDiscordBoostSystem>().CanUseDonorItem(prototype.PersonalDonor, prototype.PersonalDonorTier, session))
            return true;
        reason = prototype.PersonalDonor == DonorCategory.Boosty
            ? Loc.GetString("dl-loadout-boosty-required", ("tier", prototype.PersonalDonorTier))
            : Loc.GetString("dl-loadout-boost-required");
        return false;
    }

    private string Describe(PersonalLoadoutRequirement r)
    {
        if (r.Kind == "DiscordBoostRequirement" && !r.Inverted)
            return Loc.GetString("dl-loadout-boost-required");
        var values = r.Kind switch
        {
            "DiscordBoostRequirement" => new List<string> { Loc.GetString("dl-loadout-boost-required") },
            "CharacterJobRequirement" => r.Jobs,
            "CharacterDepartmentRequirement" => r.Departments,
            "CharacterSpeciesRequirement" => r.Species,
            "CharacterTraitRequirement" => r.Traits,
            "CharacterEmployerRequirement" => r.Employers,
            "CharacterNationalityRequirement" => r.Nationalities,
            "CharacterLifepathRequirement" => r.Lifepaths,
            _ => new List<string> { $"{r.Min}–{r.Max}" },
        };
        return $"{r.Kind}: {(r.Inverted ? "! " : "")}{string.Join(", ", values)}";
    }

    private bool Check(PersonalLoadoutRequirement r, HumanoidCharacterProfile profile, string job, IReadOnlyDictionary<string, TimeSpan> times, ICommonSession? session, int depth = 0)
    {
        if (depth > 16)
            return false;
        var result = r.Kind switch
        {
            "DiscordBoostRequirement" => EntityManager.System<SharedDiscordBoostSystem>().HasDiscordRewardAccess(session),
            "CharacterJobRequirement" => r.Jobs.Contains(job),
            "CharacterDepartmentRequirement" => _prototypes.EnumeratePrototypes<DepartmentPrototype>().Any(d => r.Departments.Contains(d.ID) && d.Roles.Any(j => j.Id == job)),
            "CharacterSpeciesRequirement" => r.Species.Contains(profile.Species.Id),
            "CharacterTraitRequirement" => r.Traits.Any(t => profile.TraitPreferences.Any(p => p.Id == t)),
            "CharacterEmployerRequirement" => r.Employers.Contains(profile.Company.ToString()),
            "CharacterNationalityRequirement" or "CharacterLifepathRequirement" => false,
            "CharacterAgeRequirement" => profile.Age >= r.Min && profile.Age <= r.Max,
            "CharacterSexRequirement" => profile.Sex.ToString() == r.Sex,
            "CharacterGenderRequirement" => profile.Gender.ToString() == r.Gender,
            "CharacterHeightRequirement" => false, // Donor uses physical centimetres, while the target profile stores sprite scale.
            "CharacterWeightRequirement" => false,
            "OverallTimeRequirement" => InRange(times.GetValueOrDefault("Overall", TimeSpan.Zero).TotalSeconds, r),
            "CharacterPlaytimeRequirement" => InRange(times.GetValueOrDefault(r.Tracker, TimeSpan.Zero).TotalSeconds, r),
            "CharacterDepartmentTimeRequirement" => InRange(_prototypes.EnumeratePrototypes<DepartmentPrototype>().Where(d => d.ID == r.Department).SelectMany(d => d.Roles).Select(j => j.Id).Distinct().Sum(j => times.GetValueOrDefault("Job" + j, TimeSpan.Zero).TotalSeconds), r),
            "CharacterLogicOrRequirement" => r.Requirements.Any(child => Check(child, profile, job, times, session, depth + 1)),
            "CharacterLogicAndRequirement" => r.Requirements.All(child => Check(child, profile, job, times, session, depth + 1)),
            "CharacterLogicXorRequirement" => r.Requirements.Count(child => Check(child, profile, job, times, session, depth + 1)) == 1,
            _ => false,
        };
        return result != r.Inverted;
    }

    private static bool InRange(double value, PersonalLoadoutRequirement r) => value >= r.Min && value <= r.Max;

    public PersonalLoadoutCustomization? Sanitize(LoadoutPrototype prototype, PersonalLoadoutCustomization? data)
    {
        if (data == null)
            return null;
        return new PersonalLoadoutCustomization
        {
            Name = Limit(data.Name, HumanoidCharacterProfile.MaxNameLength),
            Description = Limit(data.Description, HumanoidCharacterProfile.MaxDescLength),
            Color = data.Color != null && Robust.Shared.Maths.Color.TryFromHex(data.Color, out var color) ? color.ToHex() : null,
            Heirloom = prototype.PersonalHeirloom && data.Heirloom,
        };
    }

    private static string? Limit(string? value, int length)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, length)];
    }
}
