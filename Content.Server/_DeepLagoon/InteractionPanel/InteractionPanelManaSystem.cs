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

public sealed class InteractionPanelManaSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private TimeSpan _nextRefresh;
    private InteractionPanelManaPrototype Settings => _prototypes.Index<InteractionPanelManaPrototype>("InteractionPanelMana");

    public void InitializeMob(EntityUid uid)
    {
        var mana = EnsureComp<InteractionPanelManaComponent>(uid);
        RefreshMaximum(uid, mana);
    }

    public float Maximum(EntityUid uid)
    {
        var settings = Settings;
        var health = _thresholds.TryGetDeadThreshold(uid, out var threshold) ? (float) threshold.Value : settings.FallbackHealth;
        return InteractionPanelManaRules.Capacity(health, settings.HealthMultiplier, settings.FallbackHealth);
    }

    private void RefreshMaximum(EntityUid uid, InteractionPanelManaComponent mana)
    {
        var maximum = Maximum(uid);
        if (mana.Maximum == maximum) return;
        mana.Maximum = maximum;
        Dirty(uid, mana);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextRefresh) return;
        _nextRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
        // Учитываем изменение максимального здоровья, даже когда действия не выполняются.
        var query = EntityQueryEnumerator<InteractionPanelManaComponent>();
        while (query.MoveNext(out var uid, out var mana))
        {
            RefreshMaximum(uid, mana);
            if (mana.Current < mana.Maximum) continue;
            mana.Current = 0;
            Dirty(uid, mana);
            Discharge(uid, uid);
        }
    }

    public void AddMana(EntityUid source, EntityUid target, float amount)
    {
        if (!float.IsFinite(amount) || amount <= 0 || !HasComp<MobStateComponent>(target)) return;
        var mana = EnsureComp<InteractionPanelManaComponent>(target);
        RefreshMaximum(target, mana);
        var discharged = InteractionPanelManaRules.Add(ref mana.Current, amount, mana.Maximum);
        Dirty(target, mana);
        if (discharged) Discharge(source, target);
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
