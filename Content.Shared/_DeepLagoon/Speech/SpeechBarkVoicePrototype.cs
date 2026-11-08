using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Speech;

[Prototype("speechBarkVoice")]
public sealed partial class SpeechBarkVoicePrototype : IPrototype
{
    public static readonly ProtoId<SpeechBarkVoicePrototype> Default = "mutedc3";

    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string Name { get; private set; } = default!;
    [DataField(required: true)] public SoundSpecifier Sound { get; private set; } = default!;

    [DataField] public int MinSpeed { get; private set; } = 2;
    [DataField] public int MaxSpeed { get; private set; } = 8;
    [DataField] public float MinPitch { get; private set; } = 0.6f;
    [DataField] public float MaxPitch { get; private set; } = 1.4f;
    [DataField] public float MinVariance { get; private set; } = 0.1f;
    [DataField] public float MaxVariance { get; private set; } = 0.4f;
    [DataField] public bool Hidden { get; private set; }
    [DataField] public bool AllowRandom { get; private set; }

    public int MinPitchPercent => (int) MathF.Round(MinPitch * 100f);
    public int MaxPitchPercent => (int) MathF.Round(MaxPitch * 100f);
    public int MinVariancePercent => (int) MathF.Round(MinVariance * 100f);
    public int MaxVariancePercent => (int) MathF.Round(MaxVariance * 100f);
}
