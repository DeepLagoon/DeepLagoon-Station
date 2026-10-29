using Robust.Shared.Serialization;

namespace Content.Shared.Chat.TypingIndicator;

[Serializable, NetSerializable]
public enum TypingIndicatorState
{
    None = 0,
    Idle = 1,
    Typing = 2,
    EmoteIdle = 3,
    EmoteTyping = 4,
    LoocIdle = 5,
    LoocTyping = 6,
}

public static class TypingIndicatorStateExtensions
{
    public static bool IsTyping(this TypingIndicatorState state) => state is
        TypingIndicatorState.Typing or TypingIndicatorState.EmoteTyping or TypingIndicatorState.LoocTyping;

    public static bool IsIdle(this TypingIndicatorState state) => state is
        TypingIndicatorState.Idle or TypingIndicatorState.EmoteIdle or TypingIndicatorState.LoocIdle;
}
