using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentElevatorUiTests
{
    [Test]
    public async Task NativeElevatorHasKeypadPreservesEntryAndHandlesBusyAndReturnStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var panel = new NativeInterfacePanel((_, _) => { }, () => { });
            Assert.That(NativeInterfacePanel.Supports("ApartmentElevator"), Is.True);
            void Update(bool home, bool busy = false) => panel.UpdateState("ApartmentElevator",
                new TguiData().Number("number", 72519483).Bool("atHome", home).Bool("busy", busy).String("message", "").ToString());
            Button Depart() => Descendants(panel).OfType<Button>().Single(button => button.Text == "Отправка");
            Update(false);
            Assert.That(Descendants(panel).OfType<LineEdit>(), Is.Empty);
            Assert.That(Descendants(panel).OfType<Button>().Count(button => button.Text.Length == 1 && char.IsDigit(button.Text[0])), Is.EqualTo(10));
            Assert.That(Depart().Disabled, Is.True);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var keypad = typeof(NativeInterfacePanel).GetField("_elevator", flags)!.GetValue(panel)!;
            void Key(string key) => keypad.GetType().GetMethod("EnterKey", flags)!.Invoke(keypad, new object[] { key });
            foreach (var digit in "72519483") Key(digit.ToString());
            Assert.That(Depart().Disabled, Is.False);
            Update(false, true);
            Assert.That(Descendants(panel).OfType<Button>().Where(button => button.Text != "Закрыть").All(button => button.Disabled), Is.True);
            Key("clear");
            Update(false);
            Assert.That(Descendants(panel).OfType<Label>().Any(label => label.Text == "72519483"), Is.True);
            Key("backspace");
            Assert.That(Depart().Disabled, Is.True);
            Key("3");
            Assert.That(Depart().Disabled, Is.False);
            Key("clear");
            Assert.That(Depart().Disabled, Is.True);
            Key("own");
            Assert.That(Descendants(panel).OfType<Label>().Any(label => label.Text == "72519483"), Is.True);
            Assert.That(Depart().Disabled, Is.False);
            Assert.That(Descendants(panel).OfType<Button>().Any(button => button.Text == "Ввести свой номер"), Is.True);
            Update(true);
            Assert.That(Descendants(panel).OfType<Button>().Any(button => button.Text == "Вернуться на жилую остановку"), Is.True);
            Assert.That(Descendants(panel).OfType<Button>().Any(button => button.Text == "Отправка"), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
