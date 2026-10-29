using System.Globalization;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared.Chat;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.Client;

[TestFixture]
public sealed class TraitorCodewordTests
{
    [TestCase("ru-RU")]
    [TestCase("en-US")]
    public void RussianAndEnglishSpeechUseTheSameHighlighting(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var (word, upper) in new[] { ("тёмный", "ТЁМНЫЙ"), ("расчёсывать", "РАСЧЁСЫВАТЬ"), ("bright", "BRIGHT") })
            {
                foreach (var channel in new[] { ChatChannel.Local, ChatChannel.Whisper, ChatChannel.Radio })
                {
                    var text = $"{upper}, {word}!";
                    var message = new ChatMessage(channel, text, $"[bold]Агент[/bold]: {text}", NetEntity.Invalid, null);
                    message.WrappedMessage = SharedChatSystem.InjectTagAroundString(message, word, "color", "#cc3b3b");

                    Assert.That(message.WrappedMessage, Is.EqualTo(
                        $"[bold]Агент[/bold]: [color=#cc3b3b]{upper}[/color], [color=#cc3b3b]{word}[/color]!"));
                    Assert.That(message.Message, Is.EqualTo(text));
                    var payload = WebChatMessageFormatter.BuildPayload(message);
                    Assert.That(payload, Does.Contain("color:#CC3B3B"));
                    Assert.That(payload, Does.Contain(upper).And.Contain(word));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestCase("тёмный")]
    [TestCase("bright")]
    public void MarkupParametersAreNotHighlighted(string word)
    {
        var message = new ChatMessage(ChatChannel.Local, word, $"[font=\"{word}\"]{word}[/font]", NetEntity.Invalid, null);
        Assert.That(SharedChatSystem.InjectTagAroundString(message, word, "color", "#cc3b3b"),
            Is.EqualTo($"[font=\"{word}\"][color=#cc3b3b]{word}[/color][/font]"));
    }
}
