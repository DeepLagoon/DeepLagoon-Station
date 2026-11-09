using Content.Client.Clickable;
using Content.Client.Gameplay;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests.Client
{
    [TestFixture]
    public sealed class ClickMapTest
    {
        [Test]
        public void TestBasic()
        {
            var img = new Image<Rgba32>(2, 2)
            {
                [0, 0] = new(0, 0, 0, 0f),
                [1, 0] = new(0, 0, 0, 1f),
                [0, 1] = new(0, 0, 0, 1f),
                [1, 1] = new(0, 0, 0, 0f)
            };

            var clickMap = ClickMapManager.ClickMap.FromImage(img, 0.5f);

            Assert.That(clickMap.IsOccluded(0, 0), Is.False);
            Assert.That(clickMap.IsOccluded(1, 0), Is.True);
            Assert.That(clickMap.IsOccluded(0, 1), Is.True);
            Assert.That(clickMap.IsOccluded(1, 1), Is.False);
        }

        [Test]
        public void TestThreshold()
        {
            var img = new Image<Rgba32>(2, 2)
            {
                [0, 0] = new(0, 0, 0, 0f),
                [1, 0] = new(0, 0, 0, 0.25f),
                [0, 1] = new(0, 0, 0, 0.75f),
                [1, 1] = new(0, 0, 0, 1f)
            };

            var clickMap = ClickMapManager.ClickMap.FromImage(img, 0.5f);

            Assert.That(clickMap.IsOccluded(0, 0), Is.False);
            Assert.That(clickMap.IsOccluded(1, 0), Is.False);
            Assert.That(clickMap.IsOccluded(0, 1), Is.True);
            Assert.That(clickMap.IsOccluded(1, 1), Is.True);
        }

        [Test]
        public void DirectHitWinsOverNearbyForegroundSprite()
        {
            using var img = new Image<Rgba32>(5, 5)
            {
                [2, 2] = new(0, 0, 0, 1f),
            };
            var clickMap = ClickMapManager.ClickMap.FromImage(img, 0.5f);
            var foregroundHit = ClickMapManager.SampleClickMap(clickMap, (4, 2), clickMap.Size, (0, 0));
            var backgroundHit = ClickMapManager.SampleClickMap(clickMap, (2, 2), clickMap.Size, (0, 0));
            var miss = ClickMapManager.SampleClickMap(clickMap, (5, 2), clickMap.Size, (0, 0));

            Assert.That(foregroundHit, Is.EqualTo(ClickHit.Nearby));
            Assert.That(backgroundHit, Is.EqualTo(ClickHit.Direct));
            Assert.That(miss, Is.EqualTo(ClickHit.None));

            var comparer = new GameplayStateBase.ClickableEntityComparer();
            var foreground = (new EntityUid(2), foregroundHit, 2, 0u, 0f);
            var background = (new EntityUid(1), backgroundHit, 1, 0u, 0f);
            Assert.That(comparer.Compare(background, foreground), Is.LessThan(0));
            Assert.That(comparer.Compare(background, (new EntityUid(2), ClickHit.Direct, 2, 0u, 0f)), Is.GreaterThan(0));
        }
    }
}
