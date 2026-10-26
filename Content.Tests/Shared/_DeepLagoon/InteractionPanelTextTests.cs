using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Shared._DeepLagoon.InteractionPanel;
using Moq;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._DeepLagoon;

[TestFixture]
public sealed class InteractionPanelTextTests
{
    [TestCase(false, "dl-interaction-panel-result-demo")]
    [TestCase(true, "dl-interaction-panel-self-demo")]
    public void LegacyAndEmptyPoolsUseExistingLocalization(bool self, string expected)
    {
        var random = new Mock<IRobustRandom>(MockBehavior.Strict);
        var legacy = new InteractionPanelActionDefinition("demo", "all", InteractionPanelCategory.Neutral);
        Assert.That(legacy.MessageKey(self, random.Object), Is.EqualTo(expected));
        Assert.That((legacy with { Messages = [], SelfMessages = [] }).MessageKey(self, random.Object), Is.EqualTo(expected));
        random.VerifyNoOtherCalls();
    }

    [Test]
    public void EveryExecutionDrawsAgainFromTheCorrectPool()
    {
        var random = new Mock<IRobustRandom>(MockBehavior.Strict);
        random.SetupSequence(r => r.Next(2)).Returns(0).Returns(1).Returns(0);
        var action = new InteractionPanelActionDefinition("demo", "all", InteractionPanelCategory.Neutral,
            Messages: ["other-first", "other-second"], SelfMessages: ["self-first", "self-second"]);
        Assert.That(action.MessageKey(false, random.Object), Is.EqualTo("other-first"));
        Assert.That(action.MessageKey(false, random.Object), Is.EqualTo("other-second"));
        Assert.That(action.MessageKey(true, random.Object), Is.EqualTo("self-first"));
        random.Verify(r => r.Next(2), Times.Exactly(3));
    }

    [Test]
    public void SeededRuntimeRandomCanReachEveryVariant()
    {
        var random = new RobustRandom();
        random.SetSeed(17);
        var action = new InteractionPanelActionDefinition("demo", "all", InteractionPanelCategory.Neutral,
            Messages: ["first", "second", "third"]);
        var seen = new HashSet<string>(Enumerable.Range(0, 128).Select(_ => action.MessageKey(false, random)));
        Assert.That(seen, Is.EquivalentTo(action.Messages));
    }
}

[TestFixture]
public sealed class InteractionPanelTextPrototypeTests : ContentUnitTest
{
    [Test]
    public void PrototypeLoaderPassesBothPoolsToRuntimeDefinition()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        prototypes.Initialize();
        prototypes.LoadFromStream(new StringReader("""
- type: interactionPanel
  id: text-variant-test
  body: all
  category: Neutral
  flags: [INTERACTION_OTHER, INTERACTION_SELF, INTERACTION_HUMANOID]
  chatColor: "#C6E8DA"
  messages: [test-other-first, test-other-second]
  selfMessages: [test-self-first, test-self-second]
"""));
        prototypes.ResolveResults();
        var action = prototypes.Index<InteractionPanelPrototype>("text-variant-test").Definition();
        Assert.That(action.Messages, Is.EqualTo(new[] { "test-other-first", "test-other-second" }));
        Assert.That(action.SelfMessages, Is.EqualTo(new[] { "test-self-first", "test-self-second" }));
    }
}
