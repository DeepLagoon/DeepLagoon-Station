using Robust.Shared.Serialization;

namespace Content.Shared.Chat.TypingIndicator;

[Serializable, NetSerializable]
public enum TypingIndicatorVisuals : byte
{
    State,
    CharacterPrototype,
}

[Serializable]
public enum TypingIndicatorLayers : byte
{
    Base
}
