using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Client.Chat.TypingIndicator;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Chat.TypingIndicator;
using Moq;
using NUnit.Framework;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Tests.Client;

[TestFixture]
public sealed class TypingIndicatorTest
{
    [Test]
    public void InputAndTimeoutDoNotRaisePredictiveEvents()
    {
        var timing = new Mock<IGameTiming>();
        var now = TimeSpan.Zero;
        var firstPrediction = true;
        timing.SetupGet(t => t.CurTime).Returns(() => now);
        timing.SetupGet(t => t.RealTime).Returns(() => now);
        timing.SetupGet(t => t.IsFirstTimePredicted).Returns(() => firstPrediction);
        timing.SetupGet(t => t.InPrediction).Returns(false);
        var player = new Mock<IPlayerManager>();
        player.SetupGet(p => p.LocalEntity).Returns(new EntityUid(1));
        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.GetCVar(CCVars.ChatShowTypingIndicator)).Returns(true);
        var messages = new List<bool>();
        var network = new Mock<IEntityNetworkManager>();
        network.Setup(n => n.SendSystemNetworkMessage(It.IsAny<EntityEventArgs>(), true))
            .Callback<EntityEventArgs, bool>((msg, _) => messages.Add(((TypingChangedEvent) msg).State == TypingIndicatorState.Typing));
        var entities = new Mock<EntityManager>();
        entities.SetupGet(e => e.EntityNetManager).Returns(network.Object);
        var system = new TypingIndicatorSystem();
        SetField(system, typeof(TypingIndicatorSystem), "_time", timing.Object);
        SetField(system, typeof(TypingIndicatorSystem), "_playerManager", player.Object);
        SetField(system, typeof(TypingIndicatorSystem), "_cfg", config.Object);
        SetField(system, typeof(EntitySystem), "EntityManager", entities.Object);

        system.ClientChangedChatFocus(true);
        messages.Clear();
        system.ClientChangedChatText();
        Assert.That(messages, Is.EqualTo(new[] { true }));
        now = TimeSpan.FromSeconds(5);
        firstPrediction = false;
        system.Update(0);
        Assert.That(messages, Is.EqualTo(new[] { true }), "Replayed ticks must not expire UI typing state.");
        firstPrediction = true;
        system.Update(0);
        Assert.That(messages, Is.EqualTo(new[] { true, false }));
        system.ClientChangedChatText();
        system.ClientSubmittedChatText();
        Assert.That(messages, Is.EqualTo(new[] { true, false, true, false }));
        entities.Verify(e => e.RaisePredictiveEvent(It.IsAny<TypingChangedEvent>()), Times.Never);
    }

    [TestCase(ChatSelectChannel.Local, TypingIndicatorState.Idle, TypingIndicatorState.Typing)]
    [TestCase(ChatSelectChannel.Whisper, TypingIndicatorState.Idle, TypingIndicatorState.Typing)]
    [TestCase(ChatSelectChannel.Radio, TypingIndicatorState.Idle, TypingIndicatorState.Typing)]
    [TestCase(ChatSelectChannel.Emotes, TypingIndicatorState.EmoteIdle, TypingIndicatorState.EmoteTyping)]
    [TestCase(ChatSelectChannel.LOOC, TypingIndicatorState.LoocIdle, TypingIndicatorState.LoocTyping)]
    [TestCase(ChatSelectChannel.OOC, TypingIndicatorState.None, TypingIndicatorState.None)]
    [TestCase(ChatSelectChannel.Admin, TypingIndicatorState.None, TypingIndicatorState.None)]
    [TestCase(ChatSelectChannel.Console, TypingIndicatorState.None, TypingIndicatorState.None)]
    [TestCase(ChatSelectChannel.Dead, TypingIndicatorState.None, TypingIndicatorState.None)]
    [TestCase(ChatSelectChannel.Telepathic, TypingIndicatorState.None, TypingIndicatorState.None)]
    public void ChannelsHaveSeparateVisibleStates(ChatSelectChannel channel, TypingIndicatorState idle, TypingIndicatorState typing)
    {
        Assert.That(TypingIndicatorSystem.GetState(channel, true, false), Is.EqualTo(idle));
        Assert.That(TypingIndicatorSystem.GetState(channel, true, true), Is.EqualTo(typing));
        Assert.That(TypingIndicatorSystem.GetState(channel, false, true), Is.EqualTo(TypingIndicatorState.None));
    }

    [Test]
    public void ContinuousInputExtendsTimeoutWithoutSendingEveryKeystroke()
    {
        var (system, messages, timing, config) = CreateSystem();
        var now = TimeSpan.Zero;
        timing.SetupGet(t => t.RealTime).Returns(() => now);
        system.ClientChangedChatFocus(true);
        system.ClientChangedChatText();
        now = TimeSpan.FromSeconds(4);
        system.ClientChangedChatText();
        now = TimeSpan.FromSeconds(8);
        system.Update(0);
        Assert.That(messages, Is.EqualTo(new[] { TypingIndicatorState.Idle, TypingIndicatorState.Typing }));
        now = TimeSpan.FromSeconds(9);
        system.Update(0);
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.Idle));
        system.ClientChangedChatText();
        system.ClientChangedChatText(false);
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.Idle), "Erasing all text stops animation immediately.");
        system.ClientChangedChatFocus(false);
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.None));
        system.ClientChangedChatFocus(true);
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.Idle));
        config.Setup(c => c.GetCVar(CCVars.ChatShowTypingIndicator)).Returns(false);
        typeof(TypingIndicatorSystem).GetMethod("OnShowTypingChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(system, new object[] { false });
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.None), "Disabling indicators must hide Idle too.");
    }

    [Test]
    public void ChannelSwitchAndSubmitDoNotLeaveTypingBehind()
    {
        var (system, messages, _, _) = CreateSystem();
        system.ClientChangedChatFocus(true);
        system.ClientChangedChatText();
        system.ClientChangedChatChannel(ChatSelectChannel.Emotes);
        system.ClientChangedChatText();
        system.ClientChangedChatChannel(ChatSelectChannel.LOOC);
        system.ClientChangedChatText();
        system.ClientChangedChatChannel(ChatSelectChannel.OOC);
        system.ClientChangedChatText();
        Assert.That(messages, Is.EqualTo(new[] {
            TypingIndicatorState.Idle, TypingIndicatorState.Typing,
            TypingIndicatorState.EmoteIdle, TypingIndicatorState.EmoteTyping,
            TypingIndicatorState.LoocIdle, TypingIndicatorState.LoocTyping,
            TypingIndicatorState.None,
        }));
        system.ClientChangedChatChannel(ChatSelectChannel.Local);
        system.ClientChangedChatText();
        system.ClientSubmittedChatText();
        system.ClientChangedChatText(false);
        Assert.That(messages[^1], Is.EqualTo(TypingIndicatorState.None));
    }

    [Test]
    public void HeadsetPrioritySurvivesLaterClothingAndIgnoresUnequippedItems()
    {
        var ev = new BeforeShowTypingIndicatorEvent();
        Assert.That(ev.TryUpdateTimeAndIndicator("DeepLagoonMedical", TimeSpan.Zero, 10), Is.True);
        Assert.That(ev.TryUpdateTimeAndIndicator("lawyer", TimeSpan.FromSeconds(1)), Is.False);
        Assert.That(ev.TryUpdateTimeAndIndicator("DeepLagoonCargo", null, 10), Is.False);
        Assert.That(ev.GetMostRecentIndicator()!.Value.Id, Is.EqualTo("DeepLagoonMedical"));
        Assert.That(ev.TryUpdateTimeAndIndicator("DeepLagoonCargo", TimeSpan.FromSeconds(2), 10), Is.True);
        Assert.That(ev.GetMostRecentIndicator()!.Value.Id, Is.EqualTo("DeepLagoonCargo"));
    }

    private static (TypingIndicatorSystem, List<TypingIndicatorState>, Mock<IGameTiming>, Mock<IConfigurationManager>) CreateSystem()
    {
        var timing = new Mock<IGameTiming>();
        timing.SetupGet(t => t.IsFirstTimePredicted).Returns(true);
        var player = new Mock<IPlayerManager>();
        player.SetupGet(p => p.LocalEntity).Returns(new EntityUid(1));
        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.GetCVar(CCVars.ChatShowTypingIndicator)).Returns(true);
        var messages = new List<TypingIndicatorState>();
        var network = new Mock<IEntityNetworkManager>();
        network.Setup(n => n.SendSystemNetworkMessage(It.IsAny<EntityEventArgs>(), true))
            .Callback<EntityEventArgs, bool>((msg, _) => messages.Add(((TypingChangedEvent) msg).State));
        var entities = new Mock<EntityManager>();
        entities.SetupGet(e => e.EntityNetManager).Returns(network.Object);
        var system = new TypingIndicatorSystem();
        SetField(system, typeof(TypingIndicatorSystem), "_time", timing.Object);
        SetField(system, typeof(TypingIndicatorSystem), "_playerManager", player.Object);
        SetField(system, typeof(TypingIndicatorSystem), "_cfg", config.Object);
        SetField(system, typeof(EntitySystem), "EntityManager", entities.Object);
        return (system, messages, timing, config);
    }

    private static void SetField(object target, Type declaringType, string name, object value)
    {
        declaringType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
