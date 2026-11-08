using System;
using System.IO;
using Content.Server._DeepLagoon.PdaMessenger;
using Content.Shared._DeepLagoon.PdaMessenger;
using NUnit.Framework;
using Robust.Shared.Network;

namespace Content.Tests.Server;

[TestFixture]
public sealed class PdaMessengerTests
{
    [TestCase("123456789")]
    [TestCase("123-456-789")]
    [TestCase(" 123 456 789 ")]
    public void ReadsNineDigitNumbers(string input)
    {
        Assert.That(PdaMessengerLimits.TryParseNumber(input, out var number), Is.True);
        Assert.That(number, Is.EqualTo(123456789));
    }

    [TestCase("12345678")]
    [TestCase("023456789")]
    [TestCase("1234567890")]
    [TestCase("123-456-78x")]
    [TestCase("123 456 789       ")]
    public void RejectsInvalidNumbers(string input)
    {
        Assert.That(PdaMessengerLimits.TryParseNumber(input, out _), Is.False);
    }

    [Test]
    public void AddressBookSurvivesStoreRestartAndBelongsToOneProfile()
    {
#if !USE_SYSTEM_SQLITE
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"pda-messenger-{Guid.NewGuid()}.db");
        var user = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        try
        {
            int line, anotherSlot, recipient;
            using (var store = new PdaMessengerStore(path))
            {
                line = store.GetOrCreateNumber(user, 1);
                anotherSlot = store.GetOrCreateNumber(user, 2);
                recipient = store.GetOrCreateNumber(other, 3);
                store.SetContact(line, recipient, saved: true);
                store.SetContact(line, recipient, saved: true);
            }

            using (var store = new PdaMessengerStore(path))
            {
                Assert.That(store.GetOrCreateNumber(user, 1), Is.EqualTo(line));
                Assert.That(store.GetOrCreateNumber(user, 2), Is.EqualTo(anotherSlot).And.Not.EqualTo(line));
                Assert.That(store.LoadContacts(line), Is.EquivalentTo(new[] { recipient }));
                Assert.That(store.LoadContacts(anotherSlot), Is.Empty);
                Assert.That(store.LoadContacts(recipient), Is.Empty);
                store.SetContact(line, recipient, saved: false);
            }
            using var reopened = new PdaMessengerStore(path);
            Assert.That(reopened.LoadContacts(line), Is.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
