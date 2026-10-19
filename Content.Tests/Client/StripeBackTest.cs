using System.Numerics;
using System.Reflection;
using Content.Client.UserInterface.Controls;
using Moq;
using NUnit.Framework;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.Tests.Client;

[TestFixture]
public sealed class StripeBackTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(6)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(100)]
    public void SmallAllocationsKeepLayoutAndDrawingValid(int height)
    {
        foreach (var top in new[] { false, true })
        foreach (var bottom in new[] { false, true })
        foreach (var margins in new[] { false, true })
        {
            using var stripe = new StripeBack
            {
                HasTopEdge = top,
                HasBottomEdge = bottom,
                HasMargins = margins,
            };
            var child = new Control();
            stripe.AddChild(child);
            stripe.Arrange(new UIBox2(0, 0, 100, height));
            Assert.That(child.Position.Y, Is.InRange(0f, (float) height));
            Assert.That(child.Size.Y, Is.GreaterThanOrEqualTo(0f));
            Assert.That(child.Position.Y + child.Size.Y, Is.LessThanOrEqualTo(height));
            var handle = new Mock<DrawingHandleScreen>((Texture) null);
            handle.Setup(h => h.DrawRect(It.IsAny<UIBox2>(), It.IsAny<Color>(), It.IsAny<bool>()))
                .Callback<UIBox2, Color, bool>((rect, _, _) =>
                {
                    Assert.That(rect.Top, Is.InRange(0f, (float) height));
                    Assert.That(rect.Bottom, Is.InRange(rect.Top, (float) height));
                });

            // Invoke the overrides directly to reproduce allocation below DesiredSize.
            var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            typeof(StripeBack).GetMethod("MeasureOverride", flags)!.Invoke(stripe, new object[] { new Vector2(100, height) });
            typeof(StripeBack).GetMethod("ArrangeOverride", flags)!.Invoke(stripe, new object[] { new Vector2(100, height) });
            typeof(StripeBack).GetMethod("Draw", flags)!.Invoke(stripe, new object[] { handle.Object });
            var edgeCount = (top ? 1 : 0) + (bottom ? 1 : 0);
            var fits = height > 0 && height >= edgeCount * (margins ? 6 : 2);
            handle.Verify(h => h.DrawRect(It.IsAny<UIBox2>(), It.IsAny<Color>(), It.IsAny<bool>()),
                Times.Exactly(fits ? edgeCount : 0));
        }
    }
}
