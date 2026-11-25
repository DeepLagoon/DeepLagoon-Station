using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Music;

public static class MusicConstants
{
    public const string TransferKey = "DLMusicTrack";

    public const string AckTransferKey = "DLMusicAck";

    public const int MaxQueryLength = 128;
    public const int MaxIdLength = 64;
    public const int MaxTitleLength = 256;
    public const int MaxArtistLength = 256;
    public const int MaxAlbumLength = 256;
    public const int MaxMessageBytes = 64 * 1024;

    public const int MaxResults = 200;
    public const float SpeakerMaxDistance = 10f;

    public const float MaxAckSeconds = 120f;

    public const int MaxQueue = 50;
}

[Serializable, NetSerializable]
public sealed class MusicTrack
{
    public string Id { get; }
    public string Title { get; }
    public string Artist { get; }
    public float Duration { get; }
    public string Album { get; }

    public MusicTrack(string id, string title, string artist, float duration, string album = "")
    {
        Id = id;
        Title = title;
        Artist = artist;
        Duration = duration;
        Album = album;
    }
}

[Serializable, NetSerializable]
public sealed class MusicState : BoundUserInterfaceState
{
    public MusicPlaybackStatus Status { get; }
    public MusicTrack? Current { get; }
    public float Position { get; }
    public float Length { get; }
    public float Gain { get; }
    public bool Global { get; }
    public int Ready { get; }
    public int Expected { get; }

    public MusicState(MusicPlaybackStatus status, MusicTrack? current, float position, float length, float gain,
        bool global, int ready, int expected)
    {
        Status = status;
        Current = current;
        Position = position;
        Length = length;
        Gain = gain;
        Global = global;
        Ready = ready;
        Expected = expected;
    }
}

[Serializable, NetSerializable]
public enum MusicPlaybackStatus : byte
{
    Stopped,
    Downloading,
    Playing,
    Paused,
}

[Serializable, NetSerializable]
public enum MusicUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class MusicSearchRequestMessage : BoundUserInterfaceMessage
{
    public string Query { get; }

    public MusicSearchRequestMessage(string query)
    {
        Query = query;
    }
}

[Serializable, NetSerializable]
public sealed class MusicPlayRequestMessage : BoundUserInterfaceMessage
{
    public string TrackId { get; }

    public MusicPlayRequestMessage(string trackId)
    {
        TrackId = trackId;
    }
}

[Serializable, NetSerializable]
public sealed class MusicControlMessage : BoundUserInterfaceMessage
{
    public MusicControlAction Action { get; }

    public MusicControlMessage(MusicControlAction action)
    {
        Action = action;
    }
}

[Serializable, NetSerializable]
public enum MusicControlAction : byte
{
    Pause,
    Resume,
    Stop,
    Next,
}

[Serializable, NetSerializable]
public sealed class MusicSetGainMessage : BoundUserInterfaceMessage
{
    public float Gain { get; }

    public MusicSetGainMessage(float gain)
    {
        Gain = gain;
    }
}

[Serializable, NetSerializable]
public sealed class MusicSetTimeMessage : BoundUserInterfaceMessage
{
    public float Position { get; }

    public MusicSetTimeMessage(float position)
    {
        Position = position;
    }
}

[Serializable, NetSerializable]
public sealed class MusicSearchResultMessage : BoundUserInterfaceMessage
{
    public MusicTrack[] Tracks { get; }
    public MusicSearchStatus Status { get; }
    public string Query { get; }

    public MusicSearchResultMessage(MusicTrack[] tracks, MusicSearchStatus status, string query = "")
    {
        Tracks = tracks;
        Status = status;
        Query = query;
    }
}

[Serializable, NetSerializable]
public enum MusicSearchStatus : byte
{
    Ok,
    Disabled,
    BadQuery,
    NoToken,
    InvalidToken,
    RateLimited,
    UpstreamError,
}

[Serializable, NetSerializable]
public sealed class MusicTrackBeginMessage : BoundUserInterfaceMessage
{
    public MusicTrack Track { get; }
    public int TotalBytes { get; }
    public float StartDelay { get; }

    public MusicTrackBeginMessage(MusicTrack track, int totalBytes, float startDelay)
    {
        Track = track;
        TotalBytes = totalBytes;
        StartDelay = startDelay;
    }
}

[Serializable, NetSerializable]
public sealed class MusicPlaybackMessage : BoundUserInterfaceMessage
{
    public MusicPlaybackMessage(bool playing, float position)
    {
        Playing = playing;
        Position = position;
    }

    public bool Playing { get; }
    public float Position { get; }
}

public sealed class MusicPlaybackNetMessage : Robust.Shared.Network.NetMessage
{
    public MusicPlaybackCommand Command { get; set; }
    public float Position { get; set; }
    public float Gain { get; set; }
    public float StartDelay { get; set; }
    public int TotalBytes { get; set; }
    public string TrackId { get; set; } = string.Empty;
    public string ResourcePath { get; set; } = string.Empty;
    public uint Source { get; set; }
    public float MaxDistance { get; set; }

    public override MsgGroups MsgGroup => MsgGroups.Command;

    public override int EstimateBufferSize() => 96;

    public override void WriteToBuffer(Lidgren.Network.NetOutgoingMessage buffer,
        Robust.Shared.Serialization.IRobustSerializer serializer)
    {
        buffer.Write((byte) Command);
        buffer.Write(Position);
        buffer.Write(Gain);
        buffer.Write(StartDelay);
        buffer.Write(TotalBytes);
        buffer.Write(TrackId);
        buffer.Write(ResourcePath);
        buffer.Write(Source);
        buffer.Write(MaxDistance);
    }

    public override void ReadFromBuffer(Lidgren.Network.NetIncomingMessage buffer,
        Robust.Shared.Serialization.IRobustSerializer serializer)
    {
        Command = (MusicPlaybackCommand) buffer.ReadByte();
        Position = buffer.ReadFloat();
        Gain = buffer.ReadFloat();
        StartDelay = buffer.ReadFloat();
        TotalBytes = buffer.ReadInt32();
        TrackId = buffer.ReadString();
        ResourcePath = buffer.ReadString();
        Source = buffer.ReadUInt32();
        MaxDistance = buffer.ReadFloat();
    }
}

[Serializable, NetSerializable]
public enum MusicPlaybackCommand : byte
{
    Prepare,
    Play,
    Pause,
    Stop,
    Gain,
    Seek,
}

public static class MusicRules
{
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var length = Math.Min(value.Length, maxLength);

        var buffer = new char[length];
        var written = 0;
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
                continue;

            buffer[written++] = c;
        }

        return new string(buffer, 0, written).Trim();
    }

    public static bool ValidQuery(string? query, out string cleaned)
    {
        cleaned = Clean(query, MusicConstants.MaxQueryLength);
        return cleaned.Length > 0;
    }

    public static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var value = raw.Trim().Trim('"').Trim();

        if (value.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("OAuth ".Length).Trim();

        var amp = value.IndexOf('&');
        if (amp >= 0)
            value = value.Substring(0, amp);

        var space = value.IndexOf(' ');
        if (space >= 0)
            value = value.Substring(0, space);

        return value.Trim().Trim('"').Trim();
    }

    public static bool ValidTrackId(string? id, out string cleaned)
    {
        cleaned = Clean(id, MusicConstants.MaxIdLength);
        if (cleaned.Length == 0)
            return false;

        foreach (var c in cleaned)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        return true;
    }

    public static float ClampGain(float gain)
    {
        if (!float.IsFinite(gain))
            return 0f;

        return Math.Clamp(gain, 0f, 1f);
    }

    public static float ClampPosition(float position, float length)
    {
        if (!float.IsFinite(position) || length <= 0f)
            return 0f;

        return Math.Clamp(position, 0f, length);
    }

    public static float StartDelay(float slowestClientSeconds, float slack = 0.35f)
    {
        if (!float.IsFinite(slowestClientSeconds) || slowestClientSeconds < 0f)
            return slack;

        return slowestClientSeconds + slack;
    }
}
