using System.Numerics;
using System.Linq;
using Content.Client.Lobby.UI;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.Tests.Client;

[TestFixture]
public sealed class LobbyLayoutTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    [OneTimeSetUp]
    public void InitializeUi() => IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();

    [TestCase(340, 2)]
    [TestCase(220, 2)]
    [TestCase(140, 1)]
    public void UtilityButtonsReflowWithoutCrossingTheSidebar(int width, int columns)
    {
        using var grid = new LobbyUtilityGrid();
        for (var i = 0; i < 8; i++)
            grid.AddChild(new Control());
        grid.GetChild(2).Visible = false;
        grid.Measure(new Vector2(width, 600));
        grid.Arrange(UIBox2.FromDimensions(Vector2.Zero, grid.DesiredSize));
        var visible = grid.Children.Where(child => child.Visible).ToArray();
        Assert.That(grid.Width, Is.LessThanOrEqualTo(width));
        Assert.That(visible[columns].Position.Y, Is.EqualTo(LobbyConsoleTheme.RowHeight));
        foreach (var child in visible)
        {
            Assert.That(child.Position.X, Is.GreaterThanOrEqualTo(0));
            Assert.That(child.Position.X + child.Width, Is.LessThanOrEqualTo(grid.Width));
            Assert.That(child.Position.Y + child.Height, Is.LessThanOrEqualTo(grid.Height));
        }
    }

    [TestCase(1920, 1080, 400)]
    [TestCase(1280, 720, 600)]
    [TestCase(1024, 600, 465)]
    [TestCase(800, 600, 400)]
    public void CharacterAndMusicStayLeftOfUtilitiesAndRightSidebar(int width, int height, int chatWidth)
    {
        using var layout = new LobbyColumns { SidePanelWidth = chatWidth };
        var left = new Control();
        var drawer = new DrawerStub { RequestedWidth = LobbyUtilityDrawer.ExpandedWidth };
        var right = new Control();
        layout.AddChild(left);
        layout.AddChild(drawer);
        layout.AddChild(right);
        var size = new Vector2(width, height);
        layout.Measure(size);
        layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        Assert.Multiple(() =>
        {
            Assert.That(left.Position.X, Is.Zero);
            Assert.That(left.Position.X + left.Width + 16, Is.LessThanOrEqualTo(drawer.Position.X));
            Assert.That(drawer.Position.X + drawer.Width + 16, Is.EqualTo(right.Position.X));
            Assert.That(right.Position.X + right.Width, Is.EqualTo(width));
            Assert.That(right.Width, Is.LessThanOrEqualTo(chatWidth));
            Assert.That(right.Height, Is.EqualTo(height));
        });
        layout.SidePanelWidth = 100;
        layout.Measure(size);
        layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        Assert.That(right.Width, Is.EqualTo(100), "Chat width is independent of the utility drawer");
        size = new Vector2(1000, 600);
        layout.Measure(size);
        layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        Assert.That(right.Position.X + right.Width, Is.EqualTo(size.X));
    }

    [TestCase(1080)]
    [TestCase(720)]
    [TestCase(600)]
    public void FoldingUtilitiesCannotMoveCharacterMusicOrChat(int height)
    {
        using var layout = new LobbyColumns();
        var drawer = new DrawerStub { RequestedWidth = LobbyUtilityDrawer.ExpandedWidth };
        var chat = new Control();
        var left = new Control();
        layout.AddChild(left); layout.AddChild(drawer); layout.AddChild(chat);
        var size = new Vector2(1280, height);
        void Arrange() { layout.Measure(size); layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size)); }
        Arrange();
        var position = chat.Position;
        var chatSize = chat.Size;
        var leftSize = left.Size;
        drawer.RequestedWidth = LobbyUtilityDrawer.TabWidth;
        drawer.InvalidateMeasure();
        // These detached controls do not receive the UI's queued child-measure pass.
        layout.InvalidateMeasure();
        Arrange();
        Assert.That(chat.Position, Is.EqualTo(position));
        Assert.That(chat.Size, Is.EqualTo(chatSize));
        Assert.That(chat.Height, Is.EqualTo(height));
        Assert.That(left.Position, Is.EqualTo(Vector2.Zero));
        Assert.That(left.Size, Is.EqualTo(leftSize));
        Assert.That(drawer.Width, Is.EqualTo(LobbyUtilityDrawer.TabWidth));
    }

    [Test]
    public void LeftSidebarReservesTheActionsBeforeThePreview()
    {
        using var layout = new LobbyLeftPanel();
        var preview = new PreviewStub();
        var actions = new ActionsStub();
        var music = new Control { SetHeight = 188 };
        layout.AddChild(preview);
        layout.AddChild(actions);
        layout.AddChild(music);

        void Resize(int height)
        {
            var size = new Vector2(400, height);
            layout.Measure(size);
            layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
            Assert.That(actions.Position.Y + actions.Height + 8, Is.LessThanOrEqualTo(music.Position.Y));
            Assert.That(music.Position.Y + music.Height, Is.EqualTo(height));
        }

        Resize(1080);
        Assert.That(preview.Height, Is.EqualTo(515));
        Assert.That(actions.Position.Y, Is.EqualTo(527));
        Assert.That(actions.Height, Is.EqualTo(246));

        Resize(740);
        Assert.That(preview.Height, Is.EqualTo(280));
        Assert.That(actions.Position.Y, Is.EqualTo(292));
        Assert.That(actions.Height, Is.EqualTo(246));

        Resize(370);
        Assert.That(preview.Height, Is.Zero);
        Assert.That(actions.Position.Y, Is.Zero);
        Assert.That(actions.Height, Is.EqualTo(123));

        Resize(1080);
        Assert.That(preview.Height, Is.EqualTo(515));
        Assert.That(actions.Height, Is.EqualTo(246));
    }

    [TestCase(400, 1080)]
    [TestCase(400, 720)]
    [TestCase(320, 600)]
    [TestCase(180, 480)]
    public void RoundStatusStaysAboveChatWithoutChangingSidebarWidth(int width, int height)
    {
        using var layout = new LobbyRightPanel();
        var status = new Control();
        var chat = new Control { MinHeight = 120 };
        layout.AddChild(status);
        layout.AddChild(chat);
        var size = new Vector2(width, height);
        layout.Measure(size);
        layout.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        Assert.Multiple(() =>
        {
            Assert.That(chat.Height, Is.EqualTo(height * 0.85f).Within(1));
            Assert.That(chat.Position.Y + chat.Height, Is.EqualTo(height).Within(1));
            Assert.That(chat.Position.Y - status.Height, Is.EqualTo(LobbyConsoleTheme.BlockGap).Within(1));
            Assert.That(chat.Width, Is.EqualTo(width));
            Assert.That(status.Width, Is.EqualTo(width));
            Assert.That(status.Position, Is.EqualTo(Vector2.Zero));
        });
    }

    private sealed class DrawerStub : Control
    {
        public float RequestedWidth;

        protected override Vector2 MeasureOverride(Vector2 availableSize) => new(RequestedWidth, 0);
    }

    private sealed class PreviewStub : Control
    {
        protected override Vector2 MeasureOverride(Vector2 availableSize) =>
            new(302, availableSize.Y >= 515 ? 515 : availableSize.Y >= 280 ? 280 : 0);
    }

    private sealed class ActionsStub : Control
    {
        protected override Vector2 MeasureOverride(Vector2 availableSize) =>
            new(320, availableSize.Y >= 246 ? 246 : 123);
    }
}
