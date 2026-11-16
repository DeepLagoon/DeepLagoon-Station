#nullable enable

using System;
using System.IO;
using Content.Shared.Chat;
using Content.Shared.Mapping;
using Content.Shared.Preferences;
using Content.Shared.Voting;
using Lidgren.Network;
using Moq;
using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class NetworkMessageBoundsTests
{
    [TestCase(-1)]
    [TestCase(64)]
    public void CharacterUpdateRejectsInvalidPayloadLength(int length)
    {
        var buffer = CreateIncoming(message =>
        {
            message.Write(0);
            message.WriteVariableInt32(length);
        });

        Assert.Throws<InvalidOperationException>(() => new MsgUpdateCharacter().ReadFromBuffer(buffer, null!));
    }

    [Test]
    public void ChatDeletionRejectsEntityCountThatWouldOverflow()
    {
        var buffer = CreateIncoming(message =>
        {
            message.Write(0);
            message.Write(int.MinValue);
        });

        Assert.Throws<InvalidOperationException>(() => new MsgDeleteChatMessagesBy().ReadFromBuffer(buffer, null!));
    }

    [Test]
    public void WeightedVoteRoundTrips()
    {
        var sent = new MsgVoteData
        {
            VoteActive = true,
            Options = [(2, string.Empty, 3)],
            IsYourVoteDirty = true,
            YourVote = 0
        };
        var buffer = CreateIncoming(message => sent.WriteToBuffer(message, null!));
        var received = new MsgVoteData();

        received.ReadFromBuffer(buffer, null!);

        Assert.Multiple(() =>
        {
            Assert.That(received.Options, Is.EqualTo(sent.Options));
            Assert.That(received.YourVote, Is.EqualTo(sent.YourVote));
        });
    }

    [Test]
    public void MapDataRejectsTruncatedCompressedPayload()
    {
        var buffer = CreateIncoming(message =>
        {
            message.WriteVariableInt32(2);
            message.WriteVariableInt32(64);
        });

        Assert.Throws<InvalidOperationException>(() => new MappingMapDataMessage().ReadFromBuffer(buffer, null!));
    }

    [TestCase(2)]
    [TestCase(64)]
    public void MapDataVerifiesTheActualDecompressedLength(int declaredLength)
    {
        byte[] payload = [1, (byte) 'A'];
        using var context = new ZStdCompressionContext();
        var compressed = new byte[ZStd.CompressBound(payload.Length)];
        var length = context.Compress2(compressed, payload);
        var buffer = CreateIncoming(message =>
        {
            message.WriteVariableInt32(declaredLength);
            message.WriteVariableInt32(length);
            message.Write(compressed.AsSpan(0, length));
        });
        var map = new MappingMapDataMessage();
        var serializer = new Mock<IRobustSerializer>(MockBehavior.Strict);
        var expectedYaml = "A";
        serializer.Setup(s => s.DeserializeDirect(It.Is<Stream>(stream => stream.Position == 0 && stream.Length == payload.Length), out expectedYaml));

        if (declaredLength == payload.Length)
        {
            map.ReadFromBuffer(buffer, serializer.Object);
            Assert.That(map.Yml, Is.EqualTo(expectedYaml));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => map.ReadFromBuffer(buffer, serializer.Object));
        }
    }

    private static NetIncomingMessage CreateIncoming(Action<NetOutgoingMessage> write)
    {
        var peer = new NetPeer(new NetPeerConfiguration(nameof(NetworkMessageBoundsTests)));
        var outgoing = peer.CreateMessage(128);
        write(outgoing);
        var incoming = (NetIncomingMessage) Activator.CreateInstance(typeof(NetIncomingMessage), nonPublic: true)!;
        incoming.Data = outgoing.Data;
        incoming.LengthBits = outgoing.LengthBits;
        return incoming;
    }
}
