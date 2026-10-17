using System.Numerics;
using Content.Client.Lobby.UI;
using Moq;
using NUnit.Framework;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.Tests.Client;

[TestFixture]
public sealed class CharacterWindowFrameTest
{
    [TestCase(0, 0)]
    [TestCase(0, 250)]
    [TestCase(540, 0)]
    [TestCase(1, 1)]
    [TestCase(4, 100)]
    [TestCase(100, 4)]
    [TestCase(10, 10)]
    [TestCase(27, 27)]
    [TestCase(28, 28)]
    [TestCase(540, 250)]
    public void FrameStaysWithinWindowBeforeAndAfterLayout(int width, int height)
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var resizable in new[] { false, true })
        {
            var handle = new Mock<DrawingHandleScreen>((Texture) null);
            handle.Setup(h => h.DrawRect(It.IsAny<UIBox2>(), It.IsAny<Color>(), It.IsAny<bool>()))
                .Callback<UIBox2, Color, bool>((rect, _, _) =>
                {
                    Assert.That(rect.Left, Is.InRange(0f, (float) width));
                    Assert.That(rect.Top, Is.InRange(0f, (float) height));
                    Assert.That(rect.Right, Is.InRange(rect.Left, (float) width));
                    Assert.That(rect.Bottom, Is.InRange(rect.Top, (float) height));
                });
            CharacterWindowFrame.Draw(handle.Object, new Vector2(width, height), scale, resizable);
            handle.Verify(h => h.DrawRect(It.IsAny<UIBox2>(), It.IsAny<Color>(), It.IsAny<bool>()),
                width == 0 || height == 0 ? Times.Never() : Times.AtLeastOnce());
        }
    }
}
