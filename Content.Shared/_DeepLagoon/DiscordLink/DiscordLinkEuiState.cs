using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.DiscordLink;

[Serializable, NetSerializable]
public sealed class DiscordLinkEuiState(string message, string code, bool linked) : EuiStateBase
{
    public string Message = message;
    public string Code = code;
    public bool Linked = linked;
}

[Serializable, NetSerializable]
public sealed class GenerateDiscordLinkCode : EuiMessageBase;

[Serializable, NetSerializable]
public sealed class CheckDiscordLink : EuiMessageBase;
