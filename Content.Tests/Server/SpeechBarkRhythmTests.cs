using System.Linq;
using Content.Shared._DeepLagoon.Speech;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class SpeechBarkRhythmTests
{
    [Test]
    public void MatchesTheSelectedDemoRhythm()
    {
        Assert.That(SpeechBarkRhythm.Build("Капитан, вы слышите стук?"),
            Is.EqualTo(new ushort[] { 0, 137, 274, 531, 694, 831, 968, 1131 }));
        Assert.That(SpeechBarkRhythm.Build("Шлюз закрыт. Ждём у реактора."),
            Is.EqualTo(new ushort[] { 0, 163, 300, 657, 820, 983, 1120, 1257, 1394 }));
    }

    [Test]
    public void HandlesEnglishAccentsAndWordsWithoutVowels()
    {
        Assert.That(SpeechBarkRhythm.Build("hear 12345"), Is.EqualTo(new ushort[] { 0, 163, 300 }));
        Assert.That(SpeechBarkRhythm.Build("Шлю́з!!!  Ждём"), Is.EqualTo(new ushort[] { 0, 357 }));
        Assert.That(SpeechBarkRhythm.Build("…?! 😮"), Is.Empty);
    }

    [Test]
    public void TempoAndPausesControlDifferentPartsOfTheRhythm()
    {
        Assert.That(SpeechBarkRhythm.Build("Мама, да?", new SpeechBarkSettings { Speed = 3 }),
            Is.EqualTo(new ushort[] { 0, 102, 324 }));
        Assert.That(SpeechBarkRhythm.Build("Мама, да?", new SpeechBarkSettings { Pause = 150 }),
            Is.EqualTo(new ushort[] { 0, 137, 454 }));
    }

    [Test]
    public void CapsTheNoteCountAndPlaybackDuration()
    {
        Assert.That(SpeechBarkRhythm.Build(new string('а', 400)), Has.Length.EqualTo(SpeechBarkRhythm.MaxNotes));
        var separated = SpeechBarkRhythm.Build(string.Concat(Enumerable.Repeat("да. ", 50)));
        Assert.That(separated, Has.Length.LessThan(SpeechBarkRhythm.MaxNotes));
        Assert.That(separated[^1], Is.LessThanOrEqualTo(SpeechBarkRhythm.LastNoteMs));
    }
}
