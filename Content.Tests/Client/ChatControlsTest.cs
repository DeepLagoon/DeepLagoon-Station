using System.Numerics;
using System.Reflection;
using Content.Client.UserInterface.Systems.Chat.Controls;
using Moq;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.ContentPack;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.Tests.Client;

[TestFixture]
public sealed class ChatControlsTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    protected override void OverrideIoC()
    {
        base.OverrideIoC();
        var loader = new Mock<IModLoader>();
        loader.Setup(mod => mod.IsContentAssembly(typeof(TestPopup).Assembly)).Returns(true);
        IoCManager.RegisterInstance<IModLoader>(loader.Object, overwrite: true);
    }

    [OneTimeSetUp]
    public void InitializeUi() => IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();

    [TestCase(800, 600, 770, 570, false)]
    [TestCase(800, 600, 4, 570, false)]
    [TestCase(800, 600, 770, 4, false)]
    [TestCase(800, 600, 400, 570, false)]
    [TestCase(480, 400, 450, 370, false)]
    [TestCase(800, 600, 770, 570, true)]
    public void ChatPopupsFitAtScreenEdgesAndLeaveTheBottomToggleAccessible(
        int width, int height, int x, int y, bool selector)
    {
        var button = new PopupButton { Selector = selector };
        var popup = button.Popup;
        popup.AddChild(new Control { MinSize = new Vector2(360, 320) });
        var container = popup.Parent!;
        var screenSize = new Vector2(width, height);
        var screenBox = UIBox2.FromDimensions(Vector2.Zero, screenSize);
        button.Arrange(UIBox2.FromDimensions(new Vector2(x, y), new Vector2(30)));
        container.Measure(screenSize);
        container.Arrange(screenBox);

        try
        {
            button.Toggle(true);
            container.Measure(screenSize);
            container.Arrange(screenBox);
            Assert.Multiple(() =>
            {
                Assert.That(popup.Visible, Is.True);
                Assert.That(popup.Width, Is.EqualTo(360));
                Assert.That(popup.Height, Is.EqualTo(320));
                Assert.That(popup.Position.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(popup.Position.X + popup.Width, Is.LessThanOrEqualTo(width));
                Assert.That(popup.Position.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(popup.Position.Y + popup.Height, Is.LessThanOrEqualTo(height));
                if (y > height / 2)
                    Assert.That(popup.Position.Y + popup.Height, Is.LessThanOrEqualTo(y));
            });

            button.Toggle(false);
            Assert.That(popup.Visible, Is.False);
        }
        finally
        {
            popup.Close();
            popup.Orphan();
        }
    }

    public sealed class TestPopup : Popup;

    private sealed class PopupButton : ChatPopupButton<TestPopup>
    {
        public bool Selector;

        public void Toggle(bool pressed) => typeof(ChatPopupButton<TestPopup>)
            .GetMethod("OnButtonToggled", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this, new object[] { new BaseButton.ButtonToggledEventArgs(pressed, this, null!) });

        protected override UIBox2 GetPopupPosition() => UIBox2.FromDimensions(
            Selector ? GlobalPosition + new Vector2(0, Height) : GlobalPosition - new Vector2(120, 0),
            Vector2.Zero);
    }

    [Test]
    public void WheelAndBrightnessApplyOnEachChange()
    {
        var picker = new ChatColorPicker(Color.Red);
        picker.Wheel.Measure(new Vector2(150));
        picker.Wheel.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(150)));
        var changes = 0;
        var last = Color.Transparent;
        picker.OnColorChanged += color => { changes++; last = color; };
        picker.Wheel.SelectAt(new Vector2(75));
        Assert.That(changes, Is.EqualTo(1));
        Assert.That(last.R, Is.EqualTo(last.G).Within(0.01));
        picker.Brightness.Value = 0.3f;
        Assert.That(changes, Is.EqualTo(2));
        Assert.That(last.R, Is.EqualTo(0.3f).Within(0.01));
        picker.Opacity.Value = 0.5f;
        Assert.That(last.A, Is.EqualTo(0.5f).Within(0.01));
    }

    [Test]
    public void EveryMessageHasItsOwnTranslucentSurfaceAndKeepsMarkup()
    {
        var history = new ChatOutputPanel();
        var background = Color.FromHex("#20222E");
        history.MessageBackground = ChatOutputPanel.SurfaceColor(background);
        history.AddMessage(FormattedMessage.FromMarkupOrThrow("[bold]Первое сообщение[/bold]"));
        history.AddMessage(FormattedMessage.FromUnformatted("Второе сообщение"));
        Assert.That(history.EntryCount, Is.EqualTo(2));
        Assert.That(history.GetMessage(0).ToMarkup(), Does.Contain("[bold]"));
        Assert.Multiple(() =>
        {
            Assert.That(history.MessageBackground.R, Is.GreaterThan(background.R));
            Assert.That(history.MessageBackground.G, Is.GreaterThan(background.G));
            Assert.That(history.MessageBackground.B, Is.GreaterThan(background.B));
            Assert.That(history.MessageBackground.A, Is.InRange(0.01f, 0.99f));
        });
        history.RemoveEntry(^2);
        Assert.That(history.GetMessage(0).ToMarkup(), Is.EqualTo("Второе сообщение"));
        history.Clear();
        Assert.That(history.EntryCount, Is.Zero);
    }
}
