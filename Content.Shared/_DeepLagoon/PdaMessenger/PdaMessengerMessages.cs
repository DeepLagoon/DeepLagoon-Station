using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.PdaMessenger;

public static class PdaMessengerLimits
{
    public const int TextLength = 500;
    public const int ImageBytes = 256 * 1024;
    public const int ImageDimension = 1024;
    public const int ChunkBytes = 16 * 1024;
}

[Serializable, NetSerializable]
public enum PdaMessengerAction : byte { Open, Refresh, Select, Send, ReadImage, Close }

[Serializable, NetSerializable]
public sealed class PdaMessengerRequest(NetEntity pda, PdaMessengerAction action, int target = 0, string text = "",
    int uploadLength = 0) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly PdaMessengerAction Action = action;
    public readonly int Target = target;
    public readonly string Text = text;
    public readonly int UploadLength = uploadLength;
}

[Serializable, NetSerializable]
public sealed class PdaMessengerUploadChunk(NetEntity pda, int offset, byte[] bytes) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly int Offset = offset;
    public readonly byte[] Bytes = bytes;
}

[Serializable, NetSerializable]
public sealed record PdaMessengerContact(int Id, string Name, bool Available, bool Unread);

[Serializable, NetSerializable]
public sealed record PdaMessengerMessage(int Id, bool Outgoing, string Text, bool HasImage);

[Serializable, NetSerializable]
public sealed class PdaMessengerSnapshot(NetEntity pda, int peer, bool attachments,
    PdaMessengerContact[] contacts, PdaMessengerMessage[] messages, string status = "", bool sent = false) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly int Peer = peer;
    public readonly bool Attachments = attachments;
    public readonly PdaMessengerContact[] Contacts = contacts;
    public readonly PdaMessengerMessage[] Messages = messages;
    public readonly string Status = status;
    public readonly bool Sent = sent;
}

[Serializable, NetSerializable]
public sealed class PdaMessengerImageChunk(NetEntity pda, int message, int total, int offset, byte[] bytes) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly int Message = message;
    public readonly int Total = total;
    public readonly int Offset = offset;
    public readonly byte[] Bytes = bytes;
}
