using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Chat;

public static class AntighostEmote
{
    public const int MaxLength = 20_000;
    public const float Range = 5f;
}

[Serializable, NetSerializable]
public sealed class AntighostEmotePromptEvent(int requestId, string recipient) : EntityEventArgs
{
    public readonly int RequestId = requestId;
    public readonly string Recipient = recipient;
}

[Serializable, NetSerializable]
public sealed class AntighostEmoteSendEvent(int requestId, string text, bool includeNearby) : EntityEventArgs
{
    public readonly int RequestId = requestId;
    public readonly string Text = text;
    public readonly bool IncludeNearby = includeNearby;
}

[Serializable, NetSerializable]
public sealed class AntighostEmoteResultEvent(int requestId, string? errorKey = null) : EntityEventArgs
{
    public readonly int RequestId = requestId;
    public readonly string? ErrorKey = errorKey;
}
