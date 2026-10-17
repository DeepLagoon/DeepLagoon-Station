using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using Content.Client._DeepLagoon.Shuttles;
using Moq;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleRadarHardwareTests
{
    [Test]
    public async Task NativeGlyphsStayInsideTheMetalPlaque()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var glass = new ShuttleRadarGlassControl();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var fontField = typeof(ShuttleRadarGlassControl).GetField("_plateFont", flags)!;
            var realFont = (Font)fontField.GetValue(glass)!;
            var font = new Mock<Font>();
            font.Setup(f => f.GetHeight(It.IsAny<float>())).Returns((float scale) => realFont.GetHeight(scale));
            font.Setup(f => f.GetAscent(It.IsAny<float>())).Returns((float scale) => realFont.GetAscent(scale));
            font.Setup(f => f.GetDescent(It.IsAny<float>())).Returns((float scale) => realFont.GetDescent(scale));
            font.Setup(f => f.GetLineHeight(It.IsAny<float>())).Returns((float scale) => realFont.GetLineHeight(scale));
            font.Setup(f => f.GetCharMetrics(It.IsAny<Rune>(), It.IsAny<float>(), It.IsAny<bool>()))
                .Returns((Rune rune, float scale, bool fallback) => realFont.GetCharMetrics(rune, scale, fallback));
            var glyphs = new List<UIBox2>();
            font.Setup(f => f.DrawChar(It.IsAny<DrawingHandleBase>(), It.IsAny<Rune>(), It.IsAny<Vector2>(),
                    It.IsAny<float>(), It.IsAny<Color>(), It.IsAny<bool>()))
                .Returns((DrawingHandleBase _, Rune rune, Vector2 baseline, float scale, Color color, bool fallback) =>
                {
                    var metrics = realFont.GetCharMetrics(rune, scale, fallback)!.Value;
                    if (metrics.Width > 0 && metrics.Height > 0)
                        glyphs.Add(UIBox2.FromDimensions(baseline + new Vector2(metrics.BearingX, -metrics.BearingY),
                            new Vector2(metrics.Width, metrics.Height)));
                    return metrics.Advance;
                });
            fontField.SetValue(glass, font.Object);
            // Use the real engine DrawString implementation (which adds ascent)
            // and real glyph metrics; only the final GPU submission is replaced.
            var drawing = new Mock<DrawingHandleScreen>(Texture.White) { CallBase = true };
            UIBox2? plaque = null;
            drawing.Setup(h => h.DrawTextureRectRegion(It.IsAny<Texture>(), It.IsAny<UIBox2>(), It.IsAny<UIBox2?>(), It.IsAny<Color?>()))
                .Callback((Texture _, UIBox2 rect, UIBox2? region, Color? color) =>
                {
                    if (region?.Left == 1105) plaque = rect;
                });
            var draw = typeof(ShuttleRadarGlassControl).GetMethod("DrawCornerHardware", flags)!;
            foreach (var diameter in new[] { 330, 400, 560, 820 })
            {
                glyphs.Clear(); plaque = null;
                var size = new Vector2(diameter, diameter);
                glass.SetSize = size; glass.Measure(size); glass.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                draw.Invoke(glass, new object[] { drawing.Object });
                Assert.That(plaque, Is.Not.Null);
                Assert.That(glyphs, Is.Not.Empty);
                var bounds = plaque!.Value;
                foreach (var glyph in glyphs)
                {
                    Assert.That(glyph.Left, Is.GreaterThanOrEqualTo(bounds.Left - .5f));
                    Assert.That(glyph.Right, Is.LessThanOrEqualTo(bounds.Right + .5f));
                    Assert.That(glyph.Top, Is.GreaterThanOrEqualTo(bounds.Top - .5f));
                    Assert.That(glyph.Bottom, Is.LessThanOrEqualTo(bounds.Bottom + .5f),
                        $"Glyph escaped its native plaque at diameter {diameter}");
                }
            }
            glass.ShowHardware = false; glyphs.Clear(); plaque = null;
            draw.Invoke(glass, new object[] { drawing.Object });
            Assert.That(glyphs, Is.Empty);
            Assert.That(plaque, Is.Null, "Handheld scanners must keep the radar-only surface");
        });
        await pair.CleanReturnAsync();
    }
}
