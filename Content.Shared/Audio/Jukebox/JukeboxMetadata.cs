using Robust.Shared.Serialization;

namespace Content.Shared.Audio.Jukebox;

[Serializable, NetSerializable]
public sealed class JukeboxMetadataRequest(string[] tracks) : BoundUserInterfaceMessage
{
    public string[] Tracks = tracks;
}

[Serializable, NetSerializable]
public sealed class JukeboxMetadataMessage(Dictionary<string, float> lengths) : BoundUserInterfaceMessage
{
    public Dictionary<string, float> Lengths = lengths;
}
