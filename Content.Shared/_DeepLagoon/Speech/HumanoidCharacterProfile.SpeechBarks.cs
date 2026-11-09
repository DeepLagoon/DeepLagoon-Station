using Content.Shared._DeepLagoon.Speech;

namespace Content.Shared.Preferences;

public sealed partial class HumanoidCharacterProfile
{
    [DataField]
    public SpeechBarkSettings SpeechBarks { get; private set; } = new();

    public HumanoidCharacterProfile WithSpeechBarks(SpeechBarkSettings settings) =>
        new(this) { SpeechBarks = settings.Sanitize() };
}
