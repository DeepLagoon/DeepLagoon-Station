using System;
using System.Numerics;
using System.Reflection;
using System.Text;
using Content.Client.Shuttles.UI;
using Moq;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleMapFontTests
{
    [Test]
    public async Task CollapsedAndNarrowMapViewportsNeverRequestSubpixelCoordinateFonts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var map = new ShuttleMapControl { FitViewport = true };
            var font = new MinimumScaleFont();
            typeof(ShuttleMapControl).GetField("_font", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(map, font);
            var handle = new Mock<DrawingHandleScreen>(new Mock<Texture>(new Vector2i(1, 1)).Object).Object;
            var draw = typeof(ShuttleMapControl).GetMethod("DrawData", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(DrawingHandleScreen), typeof(string), typeof(Color)], null)!;
            foreach (var width in new[] { 0, 1, 5, 30, 600 })
            {
                var size = new Vector2(width, 100);
                map.Measure(size);
                map.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                Assert.DoesNotThrow(() => draw.Invoke(map, [handle, "-22616.9, 1994.3", Color.White]));
            }
            Assert.That(font.Drawn, Is.GreaterThan(0), "Normal-sized coordinate text must remain visible.");
        });
        await pair.CleanReturnAsync();
    }

    private sealed class MinimumScaleFont : Font
    {
        public int Drawn;
        private static void Check(float scale) { if (scale < 1) throw new InvalidOperationException("Invalid ppem value"); }
        public override int GetAscent(float scale) { Check(scale); return 12; }
        public override int GetDescent(float scale) { Check(scale); return 4; }
        public override int GetHeight(float scale) { Check(scale); return 16; }
        public override int GetLineHeight(float scale) { Check(scale); return 16; }
        public override CharMetrics? GetCharMetrics(Rune rune, float scale, bool fallback = true)
        { Check(scale); return new(0, 12, 8, 8, 16); }
        public override float DrawChar(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale, Color color, bool fallback = true)
        { Check(scale); Drawn++; return 8; }
        public override float DrawCharOutline(DrawingHandleBase handle, Rune rune, Vector2 baseline, float scale, TextOutline outline, bool fallback = true)
        { Check(scale); return 8; }
    }
}
