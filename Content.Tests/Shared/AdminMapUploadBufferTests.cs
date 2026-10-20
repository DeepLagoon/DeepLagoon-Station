using System;
using System.Linq;
using Content.Shared._DeepLagoon.Admin;
using NUnit.Framework;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class AdminMapUploadBufferTests
{
    [Test]
    public void MissingReorderedAndDuplicateChunksAssembleExactlyOnce()
    {
        var source = new byte[AdminMapUploadLimits.ChunkBytes * 20 + 17];
        new Random(42).NextBytes(source);
        var buffer = new AdminMapUploadBuffer(source.Length);
        var offsets = Enumerable.Range(0, 21).Select(i => i * AdminMapUploadLimits.ChunkBytes).Reverse().ToArray();
        foreach (var offset in offsets.Where(offset => offset != AdminMapUploadLimits.ChunkBytes))
        {
            var chunk = source[offset..Math.Min(source.Length, offset + AdminMapUploadLimits.ChunkBytes)];
            Assert.That(buffer.Accept(offset, chunk, out var advanced), Is.True);
            Assert.That(advanced, Is.True);
            Assert.That(buffer.Accept(offset, chunk, out advanced), Is.True);
            Assert.That(advanced, Is.False);
        }
        Assert.That(buffer.Offset, Is.EqualTo(AdminMapUploadLimits.ChunkBytes));
        Assert.That(buffer.Received, Is.EqualTo(source.Length - AdminMapUploadLimits.ChunkBytes));
        Assert.That(buffer.Accept(AdminMapUploadLimits.ChunkBytes,
            source[AdminMapUploadLimits.ChunkBytes..(2 * AdminMapUploadLimits.ChunkBytes)], out _), Is.True);
        Assert.That(buffer.Offset, Is.EqualTo(source.Length));
        Assert.That(buffer.Bytes, Is.EqualTo(source));
    }

    [TestCase(-1, 32)]
    [TestCase(int.MaxValue, 32)]
    [TestCase(1, 32)]
    [TestCase(0, 0)]
    [TestCase(0, 32769)]
    [TestCase(32768, 2)]
    public void InvalidRangesNeverModifyAcknowledgedData(int offset, int length)
    {
        var buffer = new AdminMapUploadBuffer(32769);
        Assert.That(buffer.Accept(offset, new byte[length], out var advanced), Is.False);
        Assert.That(advanced, Is.False);
        Assert.That(buffer.Received, Is.Zero);
        Assert.That(buffer.Offset, Is.Zero);
    }

    [Test]
    public void ConflictingDuplicateIsRejected()
    {
        var buffer = new AdminMapUploadBuffer(3);
        Assert.That(buffer.Accept(0, [1, 2, 3], out _), Is.True);
        Assert.That(buffer.Accept(0, [1, 9, 3], out _), Is.False);
        Assert.That(buffer.Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }
}
