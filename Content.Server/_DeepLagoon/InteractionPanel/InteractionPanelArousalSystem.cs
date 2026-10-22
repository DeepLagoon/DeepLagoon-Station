using System.Linq;
using Content.Server.Chat.Managers;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared._DeepLagoon.InteractionPanel;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MobThresholdSystem _thresholds = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IGameTiming _timing = default!;
    private TimeSpan _nextRefresh;
    private TimeSpan _lastRefresh;
    private InteractionPanelArousalPrototype Settings => _prototypes.Index<InteractionPanelArousalPrototype>(new ProtoId<InteractionPanelArousalPrototype>("InteractionPanelArousal"));

    public void InitializeMob(EntityUid uid)
    {
        var arousal = EnsureComp<InteractionPanelArousalComponent>(uid);
        RefreshMaximum(uid, arousal);
    }

    public float Maximum(EntityUid uid)
    {
        var settings = Settings;
        var health = _thresholds.TryGetDeadThreshold(uid, out var threshold) ? (float)threshold.Value : settings.FallbackHealth;
        return InteractionPanelArousalRules.Capacity(health, settings.HealthMultiplier, settings.FallbackHealth);
    }

    private void RefreshMaximum(EntityUid uid, InteractionPanelArousalComponent arousal)
    {
        var maximum = Maximum(uid);
        if (arousal.Maximum == maximum) return;
        arousal.Maximum = maximum;
        Dirty(uid, arousal);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextRefresh) return;
        var elapsed = Math.Clamp((float)(_timing.CurTime - _lastRefresh).TotalSeconds, 0, 2);
        _lastRefresh = _timing.CurTime;
        _nextRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
        // Учитываем изменение максимального здоровья, даже когда действия не выполняются.
        var query = EntityQueryEnumerator<InteractionPanelArousalComponent>();
        while (query.MoveNext(out var uid, out var arousal))
        {
            RefreshMaximum(uid, arousal);
            if (arousal.Current < arousal.Maximum)
            {
                var previous = arousal.Current;
                InteractionPanelArousalRules.Add(ref arousal.Current, -Math.Max(0, Settings.DecayPerSecond) * elapsed, arousal.Maximum);
                if (arousal.Current != previous) Dirty(uid, arousal);
                continue;
            }
            arousal.Current = 0;
            arousal.RecoveryUntil = _timing.CurTime + Settings.RecoveryDuration;
            Dirty(uid, arousal);
            Discharge(uid, uid);
        }
    }

    public void AdjustArousal(EntityUid source, EntityUid target, float amount)
    {
        if (!float.IsFinite(amount) || amount == 0 || !TryComp<MobStateComponent>(target, out var mob) || mob.CurrentState != Content.Shared.Mobs.MobState.Alive) return;
        var arousal = EnsureComp<InteractionPanelArousalComponent>(target);
        if (amount > 0 && arousal.RecoveryUntil > _timing.CurTime) return;
        RefreshMaximum(target, arousal);
        var discharged = InteractionPanelArousalRules.Add(ref arousal.Current, amount, arousal.Maximum);
        Dirty(target, arousal);
        if (discharged)
        {
            arousal.RecoveryUntil = _timing.CurTime + Settings.RecoveryDuration;
            Discharge(source, target);
        }
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
