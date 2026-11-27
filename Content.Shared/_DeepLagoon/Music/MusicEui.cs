using System.Collections.Generic;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Music;

[Serializable, NetSerializable]
public sealed class MusicEuiState : EuiStateBase
{
    public string Status { get; }
    public MusicTrack? Current { get; }
    public float Gain { get; }
    public float Length { get; }
    public float Position { get; }
    public bool Enabled { get; }
    public int Listeners { get; }
    public int Queue { get; }

    public MusicEuiState(string status, MusicTrack? current, float gain, float length, float position, bool enabled,
        int listeners, int queue)
    {
        Status = status;
        Current = current;
        Gain = gain;
        Length = length;
        Position = position;
        Enabled = enabled;
        Listeners = listeners;
        Queue = queue;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiSearchMsg : EuiMessageBase
{
    public string Query { get; }

    public MusicEuiSearchMsg(string query)
    {
        Query = query;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiControlMsg : EuiMessageBase
{
    public MusicControlAction Action { get; }

    public MusicEuiControlMsg(MusicControlAction action)
    {
        Action = action;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiPlayMsg : EuiMessageBase
{
    public string TrackId { get; }

    public MusicEuiPlayMsg(string trackId)
    {
        TrackId = trackId;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiGainMsg : EuiMessageBase
{
    public float Gain { get; }

    public MusicEuiGainMsg(float gain)
    {
        Gain = gain;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiResultsMsg : EuiMessageBase
{
    public MusicTrack[] Tracks { get; }
    public MusicSearchStatus Status { get; }

    public MusicEuiResultsMsg(MusicTrack[] tracks, MusicSearchStatus status)
    {
        Tracks = tracks;
        Status = status;
    }
}

[Serializable, NetSerializable]
public sealed class MusicEuiLabelsMsg : EuiMessageBase
{
    public Dictionary<string, string> Labels { get; }

    public MusicEuiLabelsMsg(Dictionary<string, string> labels)
    {
        Labels = labels;
    }
}
