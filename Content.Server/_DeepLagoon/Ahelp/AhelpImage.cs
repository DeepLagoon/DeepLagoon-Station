using System.IO;
using System.Linq;
using System.Xml;
using Content.Shared._DeepLagoon.Ahelp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Server._DeepLagoon.Ahelp;

internal static class AhelpImage
{
    private const int DiscordPreviewBytes = 12 * 1024;
    private const int DiscordAnimationFrames = 12;

    public static AhelpStore.Attachment? DiscordPreview(AhelpStore.Attachment original)
    {
        if (original.Bytes.Length <= DiscordPreviewBytes)
            return original;

        var gif = original.Mime == "image/gif";
        var animatedFormat = gif || original.Mime == "image/webp";
        try
        {
            var options = new DecoderOptions { MaxFrames = animatedFormat ? 120u : 1u };
            var info = Image.Identify(options, original.Bytes);
            if (info.Width > 8192 || info.Height > 8192 || (long) info.Width * info.Height > 16_000_000)
                return null;
            using var image = Image.Load<Rgba32>(options, original.Bytes);
            var animated = image.Frames.Count > 1;
            if (animated)
                PrepareAnimation(image, gif, DiscordAnimationFrames);
            image.Mutate(context => context.AutoOrient());

            using var output = new MemoryStream();
            int[] sizes = animated ? [256, 160, 112, 80, 56, 40] : [448, 384, 320, 256, 192];
            foreach (var size in sizes)
            {
                if (image.Width > size || image.Height > size)
                    image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Max }));
                image.Save(output, new WebpEncoder
                {
                    FileFormat = WebpFileFormatType.Lossy,
                    Quality = animated ? 52 : 68,
                    Method = WebpEncodingMethod.Fastest,
                    SkipMetadata = true,
                });
                if (output.Length <= DiscordPreviewBytes)
                    return new AhelpStore.Attachment("preview.webp", "image/webp", output.ToArray());
                output.SetLength(0);
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidImageContentException or UnknownImageFormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static void PrepareAnimation(Image<Rgba32> image, bool gif, int maximumFrames)
    {
        var frames = image.Frames;
        var groupSize = (frames.Count + maximumFrames - 1) / maximumFrames;
        if (gif)
            image.Metadata.GetWebpMetadata().RepeatCount = image.Metadata.GetGifMetadata().RepeatCount;
        for (var start = frames.Count - 1 - (frames.Count - 1) % groupSize; start >= 0; start -= groupSize)
        {
            uint delay = 0;
            for (var index = Math.Min(start + groupSize, frames.Count) - 1; index >= start; index--)
            {
                var frame = frames[index].Metadata;
                var duration = gif ? (uint) frame.GetGifMetadata().FrameDelay * 10 : frame.GetWebpMetadata().FrameDelay;
                delay += Math.Max(10, duration);
                if (index != start)
                    frames.RemoveFrame(index);
            }
            var metadata = frames[start].Metadata.GetWebpMetadata();
            metadata.FrameDelay = delay;
            metadata.BlendMethod = WebpBlendMethod.Source;
            metadata.DisposalMethod = WebpDisposalMethod.DoNotDispose;
        }
    }

    public static byte[]? Preview(AhelpStore.Attachment attachment)
    {
        // Formats stored without decoding, including TIFF, stay available as originals.
        if (attachment.Mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp"))
            return null;
        var animatedFormat = attachment.Mime is "image/gif" or "image/webp";
        using var image = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = animatedFormat ? 120u : 1u, SkipMetadata = true },
            attachment.Bytes);
        using var output = new MemoryStream();
        if (image.Frames.Count > 1)
        {
            PrepareAnimation(image, attachment.Mime == "image/gif", 30);
            foreach (var bounds in new[] { new Size(384, 144), new Size(288, 108), new Size(192, 72), new Size(128, 48) })
            {
                FitPreview(image, bounds);
                WritePreviewAtlas(image, output);
                if (output.Length <= AhelpLimits.Preview)
                    return output.ToArray();
                output.SetLength(0);
            }

            while (image.Frames.Count > 1)
                image.Frames.RemoveFrame(image.Frames.Count - 1);
        }

        FitPreview(image, new Size(384, 144));
        image.Save(output, new PngEncoder { SkipMetadata = true });
        return output.Length <= AhelpLimits.Preview ? output.ToArray() : null;
    }

    private static void FitPreview(Image<Rgba32> image, Size bounds)
    {
        if (image.Width > bounds.Width || image.Height > bounds.Height)
            image.Mutate(context => context.Resize(new ResizeOptions { Size = bounds, Mode = ResizeMode.Max }));
    }

    private static void WritePreviewAtlas(Image<Rgba32> image, MemoryStream output)
    {
        var count = image.Frames.Count;
        var columns = Math.Min(count, 5);
        var cellWidth = image.Width + 1;
        var cellHeight = image.Height + 1;
        using var atlas = new Image<Rgba32>(cellWidth * columns, cellHeight * ((count + columns - 1) / columns));
        for (var i = 0; i < count; i++)
        {
            var origin = new Point(i % columns * cellWidth, i / columns * cellHeight);
            using var frame = image.Frames.CloneFrame(i);
            atlas.Mutate(context => context.DrawImage(frame, origin, 1));
        }

        // AHAP, 16-bit frame dimensions and loop count, 8-bit frame count, 32-bit delays, then PNG.
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("AHAP"u8);
        writer.Write((ushort) image.Width);
        writer.Write((ushort) image.Height);
        writer.Write((ushort) image.Metadata.GetWebpMetadata().RepeatCount);
        writer.Write((byte) count);
        foreach (var frame in image.Frames)
            writer.Write(frame.Metadata.GetWebpMetadata().FrameDelay);
        atlas.Save(output, new PngEncoder { ColorType = PngColorType.Palette, SkipMetadata = true });
    }

    public static AhelpStore.Attachment Read(string name, byte[] bytes)
    {
        if (bytes.Length is 0 or > AhelpLimits.Image)
            throw new InvalidDataException();
        if (OpaqueFormat(name, bytes) is { } original)
            return Attachment(name, original.Mime, [original.Extension], bytes);

        var format = Image.DetectFormat(bytes);
        if (format.DefaultMimeType == "image/tiff")
            return Attachment(name, format.DefaultMimeType, format.FileExtensions, bytes);
        var options = new DecoderOptions { MaxFrames = 121, SkipMetadata = true };
        var info = Image.Identify(options, bytes);
        const long pixelBudget = 56_000_000;
        var pixels = (long) info.Width * info.Height;
        if (pixels is <= 0 or > 16_000_000 || info.Width > 8192 || info.Height > 8192 ||
            info.FrameMetadataCollection.Count > 120 || pixels * Math.Max(1, info.FrameMetadataCollection.Count) > pixelBudget)
            throw new InvalidDataException();

        using var input = Image.Load<Rgba32>(new DecoderOptions
        {
            MaxFrames = (uint) Math.Min(121, pixelBudget / pixels + 1),
            SkipMetadata = true,
        }, bytes);
        if (input.Frames.Count > 120 || pixels * input.Frames.Count > pixelBudget)
            throw new InvalidDataException();
        return Attachment(name, format.DefaultMimeType, format.FileExtensions, bytes);
    }

    private static AhelpStore.Attachment Attachment(string name, string mime, IEnumerable<string> extensions, byte[] bytes)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(file) || file.Length > 100 || file.AsSpan().ContainsAny('\r', '\n', '\0') ||
            !extensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            file = "image." + extensions.First();
        return new AhelpStore.Attachment(file, mime, bytes);
    }

    // Formats outside ImageSharp are transferred as originals. SVG is always a file attachment.
    private static (string Mime, string Extension)? OpaqueFormat(string name, byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.Length >= 16 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = data.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return ("image/avif", "avif");
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8) ||
                brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                return ("image/heic", "heic");
            if (brand.SequenceEqual("M4A "u8)) return ("audio/mp4", "m4a");
            if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("iso2"u8) || brand.SequenceEqual("mp41"u8) ||
                brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("M4V "u8)) return ("video/mp4", "mp4");
        }
        if (data.Length >= 16 && data.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) && name.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return ("video/webm", "webm");
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8)) return ("audio/wav", "wav");
        if (data.Length >= 16 && data.StartsWith("OggS"u8)) return ("audio/ogg", "ogg");
        if (data.Length >= 16 && data.StartsWith("fLaC"u8)) return ("audio/flac", "flac");
        if (data.Length >= 16 && (data.StartsWith("ID3"u8) || data[0] == 0xff && (data[1] & 0xe0) == 0xe0) &&
            name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return ("audio/mpeg", "mp3");
        if (data.StartsWith("8BPS"u8)) return ("image/vnd.adobe.photoshop", "psd");
        if (data.StartsWith(new byte[] { 0, 0, 1, 0 })) return ("image/x-icon", "ico");
        if (data.StartsWith(new byte[] { 0xff, 0x0a }) || data.StartsWith(new byte[] { 0, 0, 0, 12, 74, 88, 76, 32, 13, 10, 135, 10 }))
            return ("image/jxl", "jxl");
        if (!name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return null;

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = AhelpLimits.Image,
        });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg" ||
            reader.NamespaceURI != "http://www.w3.org/2000/svg")
            throw new InvalidDataException();
        while (reader.Read()) { }
        return ("image/svg+xml", "svg");
    }
}
