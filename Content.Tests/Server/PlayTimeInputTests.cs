using System;
using System.Globalization;
using Content.Server._Goobstation.Administration;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class PlayTimeInputTests
{
    [TestCase("90", 90)]
    [TestCase("1d 2h 30m", 1590)]
    [TestCase("1.5h", 90)]
    [TestCase("1,5h", 90)]
    [TestCase("1д 2ч 30м", 1590)]
    [TestCase(" 2H ", 120)]
    [TestCase("0", 0)]
    [TestCase("10m 20m", 30)]
    public void ValidInputWorksInRussianAndEnglishCultures(string input, double minutes)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "ru-RU", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.That(PlayTimeCommandUtilities.TryParseTime(input, out var time), Is.True);
                Assert.That(time, Is.EqualTo(TimeSpan.FromMinutes(minutes)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("-5h")]
    [TestCase("garbage")]
    [TestCase("1h garbage")]
    [TestCase("1h -2m")]
    [TestCase("1h 30")]
    [TestCase("NaN")]
    [TestCase("9999999999999999999999999999999999999999999d")]
    public void InvalidInputIsRejectedInsteadOfAwardingZero(string input)
    {
        Assert.That(PlayTimeCommandUtilities.TryParseTime(input, out _), Is.False);
    }
}
