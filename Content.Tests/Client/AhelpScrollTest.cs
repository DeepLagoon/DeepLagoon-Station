using System.Numerics;
using Content.Client._DeepLagoon.Ahelp;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.UnitTesting;

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
