using System.Linq;
using Content.Server._DeepLagoon.BodyCustomization;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private BodyCustomizationSystem _bodyCustomization = default!;
    [Dependency] private INetConfigurationManager _cfg = default!;
    [Dependency] private ChatSystem _chatSystem = default!;
    private TimeSpan _nextRefresh;
    private TimeSpan _lastRefresh;
    private InteractionPanelArousalPrototype Settings => _prototypes.Index<InteractionPanelArousalPrototype>(new ProtoId<InteractionPanelArousalPrototype>("InteractionPanelArousal"));

    public void InitializeMob(EntityUid uid)
    {
        var arousal = EnsureComp<InteractionPanelArousalComponent>(uid);
        RefreshMaximum(uid, arousal);
        EnsureComp<BlueMoonPhysiologyComponent>(uid);
        UpdateBodyArousal(uid, arousal);
    }

    public float Maximum(EntityUid uid) => BlueMoonArousalRules.Threshold(Preferences(uid).Tolerance);

    public BlueMoonPreferences Preferences(EntityUid uid) =>
        TryComp<InteractionPanelPreferencesComponent>(uid, out var preferences)
            ? preferences.Profile.ArousalPreferences.Sanitize() : new();

    private void RefreshMaximum(EntityUid uid, InteractionPanelArousalComponent arousal)
    {
        var preferences = Preferences(uid);
        var maximum = BlueMoonArousalRules.Threshold(preferences.Tolerance);
        var parts = ProducingParts(uid);
        if (arousal.Maximum == maximum && arousal.Physiology == preferences && arousal.ProducingParts.SequenceEqual(parts)) return;
        arousal.ProducingParts = parts;
        arousal.Maximum = maximum;
        arousal.Physiology = preferences;
        Dirty(uid, arousal);
    }

    public override void Initialize()
    {
        base.Initialize();
        InitializePhysiology();
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextRefresh) return;
        var elapsed = Math.Max(0, (float)(_timing.CurTime - _lastRefresh).TotalSeconds);
        _lastRefresh = _timing.CurTime;
        _nextRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
        var query = EntityQueryEnumerator<InteractionPanelArousalComponent>();
        while (query.MoveNext(out var uid, out var arousal))
        {
            SyncPreferences(uid, arousal);
            RefreshMaximum(uid, arousal);
            var previous = arousal.Current;
            arousal.Current = Math.Max(0, arousal.Current - Math.Max(0, Settings.DecayPerSecond) * elapsed);
            if (arousal.Current != previous) Dirty(uid, arousal);
            UpdateBodyArousal(uid, arousal);
            UpdatePhysiology(uid);
        }
    }

    private void SyncPreferences(EntityUid uid, InteractionPanelArousalComponent arousal)
    {
        if (!TryComp<ActorComponent>(uid, out var actor)) return;
        arousal.AutomaticMoans = _cfg.GetClientCVar(actor.PlayerSession.Channel, InteractionPanelCVars.AutomaticMoans);
        arousal.MoanChance = InteractionPanelArousalRules.ClampMoanChance(_cfg.GetClientCVar(actor.PlayerSession.Channel, InteractionPanelCVars.MoanChance));
        arousal.ArousalMultiplier = InteractionPanelArousalRules.ClampMultiplier(_cfg.GetClientCVar(actor.PlayerSession.Channel, InteractionPanelCVars.ArousalMultiplier));
    }

    private bool EroticEffectsAllowed(EntityUid uid) => Preferences(uid).Enabled;

    private void TryInteractionMoan(EntityUid uid, InteractionPanelArousalComponent arousal, float amount)
    {
        if (amount <= 0 || !arousal.AutomaticMoans || !EroticEffectsAllowed(uid)) return;
        var custom = TryComp<ActorComponent>(uid, out var actor)
            && _cfg.GetClientCVar(actor.PlayerSession.Channel, InteractionPanelCVars.CustomMoanChance);
        if (custom && !InteractionPanelArousalRules.CanMoan(arousal.Current, arousal.Maximum, arousal.AutomaticMoans, arousal.MoanChance)) return;
        var chance = custom ? arousal.MoanChance : BlueMoonArousalRules.MoanChance(arousal.Current, arousal.Maximum);
        if (_random.Prob(chance / 100)) _chatSystem.TryEmoteWithChat(uid, "ExtendedMoan");
    }

    public void AdjustArousal(EntityUid source, EntityUid target, float amount, bool interaction = true,
        string? bodyPart = null, string? fluidDestination = null, float? rawLimit = null)
    {
        if (!float.IsFinite(amount) || !TryComp<MobStateComponent>(target, out var mob)
            || mob.CurrentState != Content.Shared.Mobs.MobState.Alive || !EroticEffectsAllowed(target)) return;
        var arousal = EnsureComp<InteractionPanelArousalComponent>(target);
        SyncPreferences(target, arousal);
        RefreshMaximum(target, arousal);
        if (arousal.NeverAroused || amount > 0 && IsChastened(target, bodyPart)) return;
        if (rawLimit is { } limit)
        {
            if (arousal.Current < limit) arousal.Current = Math.Max(0, arousal.Current + amount);
            Dirty(target, arousal);
            UpdateBodyArousal(target, arousal);
            return;
        }
        var preferences = Preferences(target);
        var sensitivity = preferences.Sensitivity(bodyPart);
        var adjusted = amount * BlueMoonArousalRules.StimulationMultiplier(sensitivity);
        if (sensitivity == BlueMoonSensitivity.EdgingOnly && arousal.Current + adjusted >= arousal.Maximum * .8f)
            arousal.Current = arousal.Maximum * .8f - 1;
        var neutral = interaction && TryComp<ActorComponent>(target, out var actor)
            && _cfg.GetClientCVar(actor.PlayerSession.Channel, InteractionPanelCVars.NeutralArousal);
        adjusted = InteractionPanelArousalRules.Gain(adjusted, arousal.ArousalMultiplier, neutral);
        arousal.Current = Math.Max(0, arousal.Current + adjusted);
        Dirty(target, arousal);
        TryInteractionMoan(target, arousal, amount * BlueMoonArousalRules.StimulationMultiplier(sensitivity));
        if (interaction && arousal.Current > arousal.Maximum)
            TryClimax(target, source == target ? null : source, bodyPart, fluidDestination);
        UpdateBodyArousal(target, arousal);
    }

    public bool TryClimax(EntityUid uid, EntityUid? partner = null, string? bodyPart = null,
        string? destination = null, EntityUid? container = null, bool allParts = true, bool automaticCycle = true)
    {
        if (!TryComp<MobStateComponent>(uid, out var mob) || mob.CurrentState != Content.Shared.Mobs.MobState.Alive
            || !EroticEffectsAllowed(uid)) return false;
        var arousal = EnsureComp<InteractionPanelArousalComponent>(uid);
        if (arousal.NeverAroused) return false;
        var preferences = Preferences(uid);
        if (automaticCycle)
        {
            arousal.Orgasms++;
            arousal.RecoveryUntil = _timing.CurTime + TimeSpan.FromSeconds(BlueMoonArousalRules.RecoverySeconds(
                preferences.Potency, arousal.Orgasms, _random.NextFloat(30, 90), extendedBranch: true));
        }
        if (!automaticCycle || preferences.Potency == -1 || arousal.Orgasms < preferences.Potency)
            EmitClimaxFluids(uid, partner, allParts ? null : bodyPart, destination, container);
        arousal.Current = 0;
        Dirty(uid, arousal);
        Discharge(partner ?? uid, uid);
        UpdateBodyArousal(uid, arousal);
        return true;
    }

    private void UpdateBodyArousal(EntityUid uid, InteractionPanelArousalComponent arousal)
    {
        if (!EroticEffectsAllowed(uid) || arousal.NeverAroused || IsChastened(uid, "penis")) _bodyCustomization.SetGenitalArousal(uid, false);
        else if (arousal.PermanentArousal) _bodyCustomization.SetGenitalArousal(uid, true);
        else _bodyCustomization.UpdateGenitalArousal(uid, arousal.Current, arousal.Maximum);
    }

    public void PlayActionSound(EntityUid target, InteractionPanelActionDefinition action)
    {
        if (action.Sounds is { } sounds)
        {
            if (sounds.Length > 0) PlayLibrarySound(target, _random.Pick(sounds));
        }
        else if (action.Sound != null) PlayLibrarySound(target, action.Sound);
    }

    private void PlayLibrarySound(EntityUid target, string id)
    {
        if (!_prototypes.TryIndex<InteractionPanelSoundPrototype>(id, out var sound)) return;
        var path = sound.Paths is { } paths ? (paths.Length > 0 ? _random.Pick(paths) : null) : sound.Path;
        if (string.IsNullOrWhiteSpace(path)) return;
        var volume = float.IsFinite(sound.Volume) ? Math.Clamp(sound.Volume, -40, 6) : -5;
        _audio.PlayPvs(new SoundPathSpecifier(path), target, AudioParams.Default.WithVolume(volume));
    }

    private void Discharge(EntityUid source, EntityUid target)
    {
        var settings = Settings;
        var sounds = TryComp<HumanoidAppearanceComponent>(target, out var appearance) ? appearance.Sex switch
        {
            Sex.Male => settings.MaleSounds,
            Sex.Female => settings.FemaleSounds,
            Sex.Unsexed => settings.UnsexedSounds,
            _ => settings.UnknownSounds,
        } : settings.UnknownSounds;
        if (sounds.Length > 0) PlayLibrarySound(target, _random.Pick(sounds));
        var message = Loc.GetString(settings.ResetMessage, ("mob", Name(target)));
        foreach (var uid in new[] { source, target }.Distinct())
            if (TryComp<ActorComponent>(uid, out var actor))
                _chat.ChatMessageToOne(ChatChannel.Emotes, message, FormattedMessage.EscapeText(message),
                    EntityUid.Invalid, false, actor.PlayerSession.Channel, settings.ChatColor);
    }
}
