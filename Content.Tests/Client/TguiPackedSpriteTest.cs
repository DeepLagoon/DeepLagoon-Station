using System;
using System.IO;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using NUnit.Framework;
using Moq;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Tests.Client;

[TestFixture]
public sealed class TguiPackedSpriteTest
{
    [TestCase("\"directions\":4", 4)]
    [TestCase("\"directions\":4,\"delays\":[[0.1,0.2],[0.1],[0.1],[0.1]]", 5)]
    [TestCase("\"directions\":1,\"delays\":[[]]", 1)]
    public void PackedImagesUseFileCoordinatesAcrossStatesAndAtlasRows(string firstState, int offset)
    {
        using var atlas = new Image<Rgba32>(8, 8);
        var expected = new Rgba32(19, 75, 143, 255);
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 2; x++)
            atlas[offset % 4 * 2 + x, offset / 4 * 2 + y] = expected;
        atlas.Metadata.GetPngMetadata().TextData.Add(new PngTextData("robusttoolbox_rsic_meta",
            "{\"size\":{\"x\":2,\"y\":2},\"states\":[{\"name\":\"animated\"," + firstState +
            "},{\"name\":\"hair\",\"directions\":4}]}", "", ""));
        using var packed = new MemoryStream();
        atlas.SaveAsPng(packed);
        packed.Position = 0;
        using var deployed = Image.Load<Rgba32>(packed);
        packed.Position = 0;
        var rsi = LoadPackedRsi(packed);
        var url = TguiSpriteImages.PackedFrame(deployed, rsi, "hair");
        using var result = Image.Load<Rgba32>(Convert.FromBase64String(url.Split(',')[1]));
        Assert.Multiple(() =>
        {
            Assert.That(result.Width, Is.EqualTo(2));
            Assert.That(result.Height, Is.EqualTo(2));
            Assert.That(result[0, 0], Is.EqualTo(expected));
            Assert.That(result[1, 1], Is.EqualTo(expected));
        });
    }

    [Test]
    public void UnknownStateCannotReturnAnotherItemsImage()
    {
        using var atlas = new Image<Rgba32>(2, 2);
        atlas.Metadata.GetPngMetadata().TextData.Add(new PngTextData("robusttoolbox_rsic_meta",
            "{\"size\":{\"x\":2,\"y\":2},\"states\":[{\"name\":\"item\"}]}", "", ""));
        Assert.Throws<InvalidDataException>(() => TguiSpriteImages.PackedFrame(atlas,
            new RSI(new Vector2i(2, 2), new ResPath("/Textures/test.rsi")), "missing"));
    }

    private static RSI LoadPackedRsi(Stream packed)
    {
        // Use the real loader and simulate placement in a larger shared GPU atlas.
        var type = typeof(RSIResource);
        var dataType = type.GetNestedType("LoadStepData", BindingFlags.NonPublic)!;
        var data = Activator.CreateInstance(dataType, true)!;
        dataType.GetField("Path")!.SetValue(data, new ResPath("/Textures/test.rsi"));
        // The loader takes its struct by ref; reflection writes the updated value into the argument array.
        var preTextureArguments = new object[] { data, packed };
        type.GetMethod("LoadPreTextureRsic", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, preTextureArguments);
        data = preTextureArguments[0];
        dataType.GetField("AtlasOffset")!.SetValue(data, new Vector2i(80, 160));
        dataType.GetField("AtlasTexture")!.SetValue(data, new Mock<Texture>(new Vector2i(512, 512)).Object);
        var postTextureArguments = new[] { data };
        type.GetMethod("LoadPostTexture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, postTextureArguments);
        data = postTextureArguments[0];
        var result = (RSI) dataType.GetField("Rsi")!.GetValue(data)!;
        ((Image<Rgba32>) dataType.GetField("AtlasSheet")!.GetValue(data)!).Dispose();
        return result;
    }
}
