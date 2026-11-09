using Content.Shared._DeepLagoon.Speech;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared.Humanoid;
using Content.Shared.Speech;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Speech;

public sealed partial class SpeechBarkSystem : EntitySystem
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    internal bool ShouldPlay(EntityUid source, LanguagePrototype language)
    {
        if (!language.SpeechOverride.RequireSpeech ||
            !TryComp<SpeechComponent>(source, out var speech) ||
            !speech.Enabled || speech.SpeechSounds == null ||
            (speech.BarkVoice == null && !HasComp<HumanoidAppearanceComponent>(source)))
            return false;

        // Equipment and traits that replace the species' voice keep their own sound.
        return MetaData(source).EntityPrototype is { } prototype &&
            prototype.TryGetComponent<SpeechComponent>(out var defaultSpeech, EntityManager.ComponentFactory) &&
            speech.SpeechSounds == defaultSpeech.SpeechSounds;
    }

    internal void AddListener(Filter? filter, EntityUid source, ICommonSession session, EntityUid listener, int range)
    {
        // Chat can also reach cameras, portals, and distant ghosts. The bark is local to the speaker.
        if (filter != null && _transform.InRange(source, listener, range))
            filter.AddPlayer(session);
    }

    internal void Play(Filter listeners, EntityUid source, string message, byte range, bool whisper)
    {
        if (listeners.Count == 0)
            return;

        var speech = Comp<SpeechComponent>(source);
        var settings = speech.BarkSettings;
        var voice = settings.ResolveVoice(_prototypes, speech.BarkVoice);
        settings = settings.ForVoice(voice);
        var offsets = SpeechBarkRhythm.Build(message, settings);
        RaiseNetworkEvent(new SpeechBarkEvent(GetNetEntity(source), offsets, settings.RollPitches(offsets.Length, _random),
                voice.ID, range, whisper),
            listeners, recordReplay: false);
    }
}
