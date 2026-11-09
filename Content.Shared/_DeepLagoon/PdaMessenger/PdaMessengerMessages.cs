using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.PdaMessenger;

public static class PdaMessengerLimits
{
    public const int TextLength = 500;
    public const int ImageBytes = 256 * 1024;
    public const int ImageDimension = 1024;
    public const int ChunkBytes = 16 * 1024;
    public const int FirstNumber = 100_000_000;
    public const int FirstTemporaryNumber = 900_000_000;
    public const int LastNumber = 999_999_999;
    public const int NumberInputLength = 16;

    public static bool TryParseNumber(string? input, out int number)
    {
        number = 0;
        if (input == null || input.Length > NumberInputLength)
            return false;

        var digits = 0;
        foreach (var character in input)
        {
            if (character is ' ' or '-')
                continue;
            if (character is < '0' or > '9' || ++digits > 9)
                return false;
            number = number * 10 + character - '0';
        }

        return digits == 9 && number >= FirstNumber;
    }
}

[Serializable, NetSerializable]
public enum PdaMessengerAction : byte { Open, Refresh, Select, Send, ReadImage, Close, AddContact, RemoveContact }

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
public sealed record PdaMessengerContact(int Id, bool Saved, bool Available, bool Unread);

[Serializable, NetSerializable]
public sealed record PdaMessengerMessage(int Id, bool Outgoing, string Text, bool HasImage);

[Serializable, NetSerializable]
public sealed class PdaMessengerSnapshot(NetEntity pda, int number, int peer, bool attachments,
    PdaMessengerContact[] contacts, PdaMessengerMessage[] messages, string status = "", bool sent = false,
    bool contactAdded = false) : EntityEventArgs
{
    public readonly NetEntity Pda = pda;
    public readonly int Number = number;
    public readonly int Peer = peer;
    public readonly bool Attachments = attachments;
    public readonly PdaMessengerContact[] Contacts = contacts;
    public readonly PdaMessengerMessage[] Messages = messages;
    public readonly string Status = status;
    public readonly bool Sent = sent;
    public readonly bool ContactAdded = contactAdded;
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
