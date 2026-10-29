using Content.Server._EinsteinEngines.Language;
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Serialization.Manager;
using System.Linq;
using Content.Shared.Popups;

namespace Content.Server.Traits;

public sealed partial class TraitSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private SharedHandsSystem _sharedHandsSystem = default!;
    [Dependency] private EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private SharedPopupSystem _popups = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    // When the player is spawned in, add all trait components selected during character creation
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args) => ApplyTraits(args.Mob, args.JobId, args.Profile);

    public void ApplyTraits(EntityUid mob, ProtoId<JobPrototype>? jobId, HumanoidCharacterProfile profile)
    {
        // Check if player's job allows to apply traits
        if (jobId == null ||
            !_prototypeManager.TryIndex<JobPrototype>(jobId ?? string.Empty, out var protoJob) ||
            !protoJob.ApplyTraits)
        {
            return;
        }

        if (!_config.GetCVar(CCVars.GameTraitsEnabled)) return;
        var status = TraitSelection.Evaluate(profile, _prototypeManager, _config, jobId);
        if (!status.Valid)
        {
            Log.Warning($"Rejected trait selection for {ToPrettyString(mob)}: {string.Join("; ", status.Reasons)}");
            _popups.PopupEntity(Loc.GetString("trait-selection-rejected"), mob, mob, PopupType.LargeCaution);
            return;
        }

        var traits = profile.TraitPreferences.Select(id => _prototypeManager.Index(id))
            .OrderBy(t => t.Priority).ThenBy(t => t.ID, StringComparer.Ordinal).ToArray();
        // Validate entity restrictions before applying any effects or spending disadvantage points.
        if (traits.Any(t => _whitelistSystem.IsWhitelistFail(t.Whitelist, mob)
                            || _whitelistSystem.IsBlacklistPass(t.Blacklist, mob)))
        {
            _popups.PopupEntity(Loc.GetString("trait-selection-rejected"), mob, mob, PopupType.LargeCaution);
            return;
        }
        foreach (var traitPrototype in traits)
        {
            if (traitPrototype.Disabled)
                continue;

            if (_whitelistSystem.IsWhitelistFail(traitPrototype.Whitelist, mob) ||
                _whitelistSystem.IsBlacklistPass(traitPrototype.Blacklist, mob))
                continue;

            // Add all components required by the prototype
            EntityManager.AddComponents(mob, traitPrototype.Components, false);
            foreach (var function in traitPrototype.Functions)
                function.OnPlayerSpawn(mob, _factory, EntityManager, _serialization);

            // Einstein Engines - Language begin (remove this if trait system refactor)
            // Remove/Add Languages required by the prototype
            var language = EntityManager.System<LanguageSystem>();

            if (traitPrototype.RemoveLanguagesSpoken is not null)
                foreach (var lang in traitPrototype.RemoveLanguagesSpoken)
                    language.RemoveLanguage(mob, lang, true, false);

            if (traitPrototype.RemoveLanguagesUnderstood is not null)
                foreach (var lang in traitPrototype.RemoveLanguagesUnderstood)
                    language.RemoveLanguage(mob, lang, false, true);

            if (traitPrototype.LanguagesSpoken is not null)
                foreach (var lang in traitPrototype.LanguagesSpoken)
                    language.AddLanguage(mob, lang, true, false);

            if (traitPrototype.LanguagesUnderstood is not null)
                foreach (var lang in traitPrototype.LanguagesUnderstood)
                    language.AddLanguage(mob, lang, false, true);
            // Einstein Engines - Language end

            // Add item required by the trait
            if (traitPrototype.TraitGear == null)
                continue;

            if (!TryComp(mob, out HandsComponent? handsComponent))
                continue;

            var coords = Transform(mob).Coordinates;
            var inhandEntity = Spawn(traitPrototype.TraitGear, coords);
            _sharedHandsSystem.TryPickup(mob,
                inhandEntity,
                checkActionBlocker: false,
                handsComp: handsComponent);
        }
    }
}
