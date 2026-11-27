using System.IO;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._Mono.Radar;

// Radar telemetry is a replaceable snapshot. It must not wait behind reliable entity effects
// or for a PVS state tick. Polls may be lost; late/out-of-order snapshots are discarded by sample time.
public sealed class MsgRadarPoll : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.Unreliable;
    public NetEntity Radar;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) => Radar = new(buffer.ReadInt32());
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) => buffer.Write(Radar.Id);
}

public sealed class MsgRadarSnapshot : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    // Unordered delivery avoids head-of-line blocking by gun effects. Reliability also allows
    // ordinary radar snapshots to exceed the UDP MTU without relying on IP fragmentation.
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableUnordered;
    public GiveBlipsEvent Snapshot = default!;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        var length = buffer.ReadVariableInt32();
        if (length is < 1 or > 262144) throw new InvalidDataException("Invalid radar snapshot length");
        buffer.AssertAtLeastSize(length);
        using var stream = new MemoryStream(length);
        buffer.ReadAlignedMemory(stream, length);
        Snapshot = serializer.Deserialize<GiveBlipsEvent>(stream);
    }
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        using var stream = new MemoryStream();
        serializer.Serialize(stream, Snapshot);
        buffer.WriteVariableInt32((int) stream.Length);
        buffer.Write(stream.AsSpan());
    }
}
