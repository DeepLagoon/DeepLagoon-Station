using System;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Chat;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Tests.Client;

[TestFixture]
public sealed class WebChatMessageCacheTests
{
    [Test]
    public void HistoryReusesFormattedPayloadAndInvalidatesChangedMessageFields()
    {
        var cache = new WebChatMessageCache();
        var message = new ChatMessage(ChatChannel.Local, "one", "[bold]one[/bold]", NetEntity.Invalid, null);
        var original = cache.GetPayload(message);
        Assert.That(cache.GetPayload(message), Is.SameAs(original));
        message.Read = true;
        Assert.That(cache.GetPayload(message), Is.SameAs(original));
        message.WrappedMessage = "two";
        Assert.That(cache.GetPayload(message), Is.EqualTo(WebChatMessageFormatter.BuildPayload(message)).And.Not.EqualTo(original));
        message.Channel = ChatChannel.OOC;
        message.MessageColorOverride = Color.Red;
        Assert.That(cache.GetPayload(message), Is.EqualTo(WebChatMessageFormatter.BuildPayload(message)));
        var retained = cache.GetPayload(message);
        cache.Retain(new[] { message });
        Assert.That(cache.GetPayload(message), Is.SameAs(retained));
        cache.Retain(Array.Empty<ChatMessage>());
        Assert.That(cache.GetPayload(message), Is.EqualTo(retained).And.Not.SameAs(retained));
    }
}
