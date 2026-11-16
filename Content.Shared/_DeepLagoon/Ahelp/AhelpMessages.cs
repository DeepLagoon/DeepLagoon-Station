using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Ahelp;

public static class AhelpLimits
{
    public const int Text = 1800;
    public const int Image = 20 * 1024 * 1024;
    public const int Chunk = 32 * 1024;
    public const int Preview = 256 * 1024;
    public const int Page = 30;
}

[Serializable, NetSerializable]
public enum AhelpStatus : byte { New, Claimed, Closing, Resolved, Rejected, Closed }

[Serializable, NetSerializable]
public enum AhelpAction : byte { Read, Send, Claim, Resolve, Reject, Close, Attachment, Preview }

[Serializable, NetSerializable]
public sealed record AhelpTicket(Guid Id, long Number, Guid Player, string PlayerName, Guid? Assignee,
    string? AssigneeName, AhelpStatus Status, long Created, bool Unread = false);

[Serializable, NetSerializable]
public sealed record AhelpLine(long Id, Guid Author, string AuthorName, string Text, bool Staff, long At,
    string? File = null, string? Mime = null);

[Serializable, NetSerializable]
public sealed class AhelpRequest(AhelpAction action, Guid ticket, Guid player = default, Guid operation = default,
    string text = "", bool staff = false, bool sound = true, string file = "", int size = 0,
    long before = 0, long attachment = 0) : EntityEventArgs
{
    public readonly AhelpAction Action = action;
    public readonly Guid Ticket = ticket;
    public readonly Guid Player = player;
    public readonly Guid Operation = operation;
    public readonly string Text = text;
    public readonly bool Staff = staff;
    public readonly bool Sound = sound;
    public readonly string File = file;
    public readonly int Size = size;
    public readonly long Before = before;
    public readonly long Attachment = attachment;
}

[Serializable, NetSerializable]
public sealed class AhelpUpload(Guid operation, int offset, byte[] bytes) : EntityEventArgs
{
    public readonly Guid Operation = operation;
    public readonly int Offset = offset;
    public readonly byte[] Bytes = bytes;
}

[Serializable, NetSerializable]
public sealed class AhelpDownload(long attachment, int offset, int size, byte[] bytes, bool preview = false) : EntityEventArgs
{
    public readonly long Attachment = attachment;
    public readonly int Offset = offset;
    public readonly int Size = size;
    public readonly byte[] Bytes = bytes;
    public readonly bool Preview = preview;
}

[Serializable, NetSerializable]
public sealed class AhelpSnapshot(bool enabled, AhelpTicket[] tickets, AhelpTicket? selected,
    AhelpLine[] lines, long before, string error = "", Guid operation = default) : EntityEventArgs
{
    public readonly bool Enabled = enabled;
    public readonly AhelpTicket[] Tickets = tickets;
    public readonly AhelpTicket? Selected = selected;
    public readonly AhelpLine[] Lines = lines;
    public readonly long Before = before;
    public readonly string Error = error;
    public readonly Guid Operation = operation;
}

[Serializable, NetSerializable]
public sealed class AhelpNotice(Guid ticket, long eventId, bool adminSound, bool playerSound, bool remind,
    string result = "") : EntityEventArgs
{
    public readonly Guid Ticket = ticket;
    public readonly long EventId = eventId;
    public readonly bool AdminSound = adminSound;
    public readonly bool PlayerSound = playerSound;
    public readonly bool Remind = remind;
    public readonly string Result = result;
}
