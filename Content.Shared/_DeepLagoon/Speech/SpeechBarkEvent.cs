using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Speech;

[Serializable, NetSerializable]
public sealed class SpeechBarkEvent(
    NetEntity source,
    ushort[] offsets,
    float[] pitches,
    ProtoId<SpeechBarkVoicePrototype> voice,
    byte range,
    bool whisper) : EntityEventArgs
{
    public readonly NetEntity Source = source;
    public readonly ushort[] Offsets = offsets;
    public readonly float[] Pitches = pitches;
    public readonly ProtoId<SpeechBarkVoicePrototype> Voice = voice;
    public readonly byte Range = range;
    public readonly bool Whisper = whisper;
}
