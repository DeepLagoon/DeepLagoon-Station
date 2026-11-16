using System.IO;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared.Mapping;

public sealed class MappingMapDataMessage : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableUnordered;

    public ZStdCompressionContext Context = default!;
    public string Yml = default!;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        MsgSize = buffer.LengthBytes;

        var uncompressedLength = buffer.ReadVariableInt32();
        if (uncompressedLength <= 0)
            throw new InvalidDataException("Invalid uncompressed map length.");

        var compressedLength = buffer.ReadVariableInt32();
        buffer.AssertAtLeastSize(compressedLength);
        using var stream = new MemoryStream(compressedLength);
        buffer.ReadAlignedMemory(stream, compressedLength);
        using var decompress = new ZStdDecompressStream(stream);
        // The declared length must not determine an allocation or the copy buffer size.
        using var decompressed = new MemoryStream();
        decompress.CopyTo(decompressed);
        if (decompressed.Length != uncompressedLength)
            throw new InvalidDataException("Decompressed map length differs from its header.");

        decompressed.Position = 0;
        serializer.DeserializeDirect(decompressed, out Yml);
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        var stream = new MemoryStream();
        serializer.SerializeDirect(stream, Yml);
        buffer.WriteVariableInt32((int) stream.Length);

        stream.Position = 0;
        var buf = new byte[ZStd.CompressBound((int) stream.Length)];
        var length = Context.Compress2(buf, stream.AsSpan());

        buffer.WriteVariableInt32(length);
        buffer.Write(buf.AsSpan(0, length));
    }
}
