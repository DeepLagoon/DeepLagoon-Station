namespace Content.Shared._DeepLagoon.Admin;

/// <summary>Fixed-size, bounded, out-of-order assembly with an idempotent cumulative acknowledgement.</summary>
public sealed class AdminMapUploadBuffer
{
    public byte[] Bytes { get; }
    private readonly bool[] _received;
    public int Offset { get; private set; }
    public int Received { get; private set; }

    public AdminMapUploadBuffer(int length)
    {
        if (length is < 1 or > AdminMapUploadLimits.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(length));
        Bytes = new byte[length];
        _received = new bool[(length + AdminMapUploadLimits.ChunkBytes - 1) / AdminMapUploadLimits.ChunkBytes];
    }

    public bool Accept(int offset, byte[]? bytes, out bool advanced)
    {
        advanced = false;
        if (bytes == null || offset < 0 || offset >= Bytes.Length || offset % AdminMapUploadLimits.ChunkBytes != 0 ||
            bytes.Length != Math.Min(AdminMapUploadLimits.ChunkBytes, Bytes.Length - offset))
            return false;
        var index = offset / AdminMapUploadLimits.ChunkBytes;
        if (_received[index])
            return Bytes.AsSpan(offset, bytes.Length).SequenceEqual(bytes);
        bytes.CopyTo(Bytes, offset);
        _received[index] = true;
        Received += bytes.Length;
        advanced = true;
        while (Offset < Bytes.Length && _received[Offset / AdminMapUploadLimits.ChunkBytes])
            Offset += Math.Min(AdminMapUploadLimits.ChunkBytes, Bytes.Length - Offset);
        return true;
    }
}
