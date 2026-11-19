using System.Numerics;
using System.Linq;
using System.Text;
using Content.Client._DeepLagoon.Ahelp;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Graphics;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.UnitTesting;
using static Robust.Client.UserInterface.StylesheetHelpers;

namespace Content.Tests.Client;

[TestFixture]
public sealed class AhelpScrollTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    [OneTimeSetUp]
    public void InitializeUi() => IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();

    [Test]
    public void FollowingLatestMovesHistoryInTheSameLayoutPass()
    {
        var history = new Control { MinHeight = 400 };
        var scroll = new AhelpWindow.TicketHistoryScroll { HScrollEnabled = false, Children = { history } };
        var viewport = UIBox2.FromDimensions(Vector2.Zero, new Vector2(240, 100));

        scroll.Arrange(viewport);
        scroll.VScroll = 40;
        scroll.Arrange(viewport);
        Assert.That(history.Position.Y, Is.EqualTo(-40));

        scroll.FollowLatest();
        scroll.Arrange(viewport);
        Assert.That(scroll.VScroll, Is.EqualTo(300));
        Assert.That(history.Position.Y + history.Height, Is.EqualTo(scroll.Height));

        history.MinHeight = 500;
        scroll.FollowLatest();
        scroll.Arrange(viewport);
        Assert.That(scroll.VScroll, Is.EqualTo(400));
        Assert.That(history.Position.Y + history.Height, Is.EqualTo(scroll.Height));
    }

    [Test]
    public void NativeHistoryWrapsTextAndUnbrokenFileNamesAtTheAvailableWidth()
    {
        var text = AhelpWindow.WrappedText(string.Concat(Enumerable.Repeat("Длинное сообщение. ", 25)));
        var file = AhelpWindow.WrappedText(new string('a', 200) + ".png");
        var history = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true, Children = { text, file } };
        var scroll = new AhelpWindow.TicketHistoryScroll
        {
            Stylesheet = new Stylesheet(new StyleRule[] { Element<RichTextLabel>().Prop("font", new LayoutFont()) }),
            Children = { history },
        };
        scroll.ForceRunStyleUpdate();
        scroll.Measure(new Vector2(420, 100));
        scroll.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(420, 100)));
        var textHeight = text.Height;
        var fileHeight = file.Height;
        Assert.That(textHeight, Is.GreaterThan(16));
        Assert.That(scroll.HScrollEnabled, Is.False);
        scroll.Measure(new Vector2(220, 100));
        scroll.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(220, 100)));
        Assert.That(text.Height, Is.GreaterThan(textHeight));
        Assert.That(file.Height, Is.GreaterThan(fileHeight));
        Assert.That(text.Width, Is.LessThanOrEqualTo(scroll.Width));
        Assert.That(file.Width, Is.LessThanOrEqualTo(scroll.Width));
        Assert.That(history.Width, Is.LessThanOrEqualTo(scroll.Width));
    }

    private sealed class LayoutFont : Font
    {
        public override int GetAscent(float scale) => (int) (12 * scale);
        public override int GetHeight(float scale) => (int) (16 * scale);
        public override int GetDescent(float scale) => (int) (4 * scale);
        public override int GetLineHeight(float scale) => (int) (16 * scale);
        public override CharMetrics? GetCharMetrics(Rune rune, float scale, bool fallback = true)
            => new(0, (int) (12 * scale), (int) (8 * scale), (int) (8 * scale), (int) (16 * scale));
        public override float DrawChar(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale, Color color, bool fallback = true) => 8 * scale;
        public override float DrawCharOutline(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale, TextOutline outline, bool fallback = true) => 8 * scale;
    }

    [TestCase(0, 0, 0)]
    [TestCase(149, 0, 0)]
    [TestCase(150, 0, 1)]
    [TestCase(399, 0, 1)]
    [TestCase(400, 0, 0)]
    [TestCase(400, 1, 1)]
    public void ThumbnailSelectsTheFrameForItsElapsedTime(int elapsed, int loops, int frame)
    {
        Assert.That(AhelpThumbnail.SelectFrame([150, 400], loops, elapsed), Is.EqualTo(frame));
    }
}
