using System.Linq;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class RadioChannelPrefixTests
{
    [Test]
    public async Task CyrillicPrefixesResolveAndSurviveSanitization()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var entities = pair.Server.ResolveDependency<IEntityManager>();
            var chat = entities.System<ChatSystem>();
            var channels = prototypes.EnumeratePrototypes<RadioChannelPrototype>().ToArray();
            Assert.That(channels.Select(channel => channel.KeyCode).Distinct().Count(), Is.EqualTo(channels.Length));
            Assert.That(channels.Any(channel => channel.KeyCode == SharedChatSystem.DefaultChannelKey), Is.False);
            Assert.That(prototypes.Index<RadioChannelPrototype>("Traffic").KeyCode, Is.EqualTo('л'));

            const string message = "Я говорю по радио!";
            foreach (var channel in channels)
            {
                // Handheld radios have no typed channel prefix; Common uses a semicolon.
                if (channel.KeyCode == '\0' || channel.ID == SharedChatSystem.CommonChannel)
                    continue;

                Assert.That(channel.KeyCode, Is.InRange('а', 'я'), channel.ID);
                foreach (var key in new[] { channel.KeyCode, char.ToUpperInvariant(channel.KeyCode) })
                foreach (var separator in new[] { SharedChatSystem.RadioChannelPrefix, SharedChatSystem.RadioChannelAltPrefix })
                {
                    var prefix = $"{separator}{key}";
                    var input = $"{prefix} {message}";
                    chat.GetRadioKeycodePrefix(EntityUid.Invalid, input, out var body, out var extractedPrefix);
                    Assert.That(extractedPrefix, Is.EqualTo(prefix), channel.ID);
                    Assert.That(body, Is.EqualTo($" {message}"), channel.ID);
                    Assert.That(chat.TryProccessRadioMessage(EntityUid.Invalid, input, out var output, out var resolved, quiet: true), Is.True);
                    Assert.That(resolved?.ID, Is.EqualTo(channel.ID));
                    Assert.That(output, Is.EqualTo(message));
                }
            }

            Assert.That(chat.TryProccessRadioMessage(EntityUid.Invalid, $"; {message}", out var commonMessage, out var common, quiet: true), Is.True);
            Assert.That(common?.ID, Is.EqualTo("Common"));
            Assert.That(commonMessage, Is.EqualTo(message));
            Assert.That(SharedChatSystem.DefaultChannelPrefix, Is.EqualTo(":г"));
        });
        await pair.CleanReturnAsync();
    }
}
