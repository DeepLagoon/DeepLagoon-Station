using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Speech;

[Serializable, NetSerializable, DataDefinition]
public sealed partial record SpeechBarkSettings
{
    public const int DefaultSpeed = 4;
    public const int MinPause = 25;
    public const int MaxPause = 200;

    [DataField] public string Voice { get; set; } = "";
    [DataField] public int Speed { get; set; } = DefaultSpeed;
    [DataField] public int PitchPercent { get; set; } = 100;
    [DataField] public int VariancePercent { get; set; } = 20;
    [DataField] public int Pause { get; set; } = 100;

    public SpeechBarkSettings Sanitize() => this with
    {
        Voice = Voice is { Length: <= 64 } ? Voice : "",
        Speed = Math.Clamp(Speed, 1, 100),
        PitchPercent = Math.Clamp(PitchPercent, 1, 400),
        VariancePercent = Math.Clamp(VariancePercent, 0, 100),
        Pause = Math.Clamp(Pause, MinPause, MaxPause),
    };

    public SpeechBarkSettings ForVoice(SpeechBarkVoicePrototype voice) => this with
    {
        Speed = Math.Clamp(Speed, voice.MinSpeed, voice.MaxSpeed),
        PitchPercent = Math.Clamp(PitchPercent, voice.MinPitchPercent, voice.MaxPitchPercent),
        VariancePercent = Math.Clamp(VariancePercent, voice.MinVariancePercent, voice.MaxVariancePercent),
        Pause = Math.Clamp(Pause, MinPause, MaxPause),
    };

    public SpeechBarkVoicePrototype ResolveVoice(IPrototypeManager prototypes, ProtoId<SpeechBarkVoicePrototype>? fallback)
    {
        if (Voice.Length != 0 && prototypes.TryIndex<SpeechBarkVoicePrototype>(Voice, out var selected) && !selected.Hidden)
            return selected;

        return prototypes.Index(fallback ?? SpeechBarkVoicePrototype.Default);
    }

    public float[] RollPitches(int count, IRobustRandom random)
    {
        var pitches = new float[count];
        for (var i = 0; i < pitches.Length; i++)
            pitches[i] = (PitchPercent + random.NextFloat(-0.5f, 0.5f) * VariancePercent) / 100f;

        return pitches;
    }
}
