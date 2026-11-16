using System.IO;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Ahelp;

internal sealed record AhelpThumbnailData(string Image, int Width, int Height, int Loops, int[] FrameEnds)
{
    public TguiData ToWeb(long id) => new TguiData().Number("id", id).String("image", $"data:image/png;base64,{Image}")
        .Number("width", Width).Number("height", Height).Number("loops", Loops)
        .Array("frameEnds", FrameEnds.Select(end => new TguiData().Number("time", end)));

    public static AhelpThumbnailData? Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 'A' || data[1] != 'H' || data[2] != 'A' || data[3] != 'P')
            return new AhelpThumbnailData(Convert.ToBase64String(data), 0, 0, 1, []);

        // AHAP metadata precedes the PNG atlas. Read little-endian integers from the wire.
        if (data.Length < 11)
            return null;
        var width = data[4] | (data[5] << 8);
        var height = data[6] | (data[7] << 8);
        var loops = data[8] | (data[9] << 8);
        var count = data[10];
        var pngStart = 11 + count * 4;
        if (width is < 1 or > 384 || height is < 1 or > 144 || count is < 2 or > 30 || data.Length <= pngStart)
            return null;
        var ends = new int[count];
        var duration = 0;
        for (var i = 0; i < count; i++)
        {
            var offset = 11 + i * 4;
            var delay = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
            if (delay is < 10 or > 16_777_215)
                return null;
            duration += delay;
            ends[i] = duration;
        }
        return new AhelpThumbnailData(Convert.ToBase64String(data, pngStart, data.Length - pngStart), width, height, loops, ends);
    }
}

internal sealed class AhelpThumbnail : IDisposable
{
    private readonly OwnedTexture _texture;
    private readonly Texture[] _frames;
    private readonly int[] _frameEnds;
    private readonly int _loops;
    private readonly IGameTiming _timing;
    private readonly TimeSpan _started;

    private AhelpThumbnail(OwnedTexture texture, Texture[] frames, int[] frameEnds, int loops, IGameTiming timing)
    {
        _texture = texture;
        _frames = frames;
        _frameEnds = frameEnds;
        _loops = loops;
        _timing = timing;
        _started = timing.RealTime;
    }

    public static AhelpThumbnail Load(AhelpThumbnailData data, IClyde clyde, IGameTiming timing)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(data.Image));
        var texture = clyde.LoadTextureFromPNGStream(stream, "Ahelp image");
        var count = data.FrameEnds.Length;
        if (count == 0)
            return new AhelpThumbnail(texture, [texture], [1], 1, timing);

        var columns = Math.Min(count, 5);
        var cellWidth = data.Width + 1;
        var cellHeight = data.Height + 1;
        if (texture.Width != cellWidth * columns || texture.Height != cellHeight * ((count + columns - 1) / columns))
        {
            texture.Dispose();
            throw new InvalidDataException("Invalid Ahelp atlas dimensions");
        }

        var frames = new Texture[count];
        for (var i = 0; i < count; i++)
        {
            var x = (i % columns) * cellWidth;
            var y = (i / columns) * cellHeight;
            frames[i] = new AtlasTexture(texture,
                UIBox2.FromDimensions(new Vector2(x, y), new Vector2(data.Width, data.Height)));
        }
        return new AhelpThumbnail(texture, frames, data.FrameEnds, data.Loops, timing);
    }

    internal static int SelectFrame(int[] frameEnds, int loops, double elapsed)
    {
        var duration = frameEnds[^1];
        if (loops > 0 && elapsed >= (double) duration * loops)
            return frameEnds.Length - 1;
        var position = (int) (Math.Max(0, elapsed) % duration);
        var index = 0;
        while (position >= frameEnds[index])
            index++;
        return index;
    }

    public Control CreateControl() => new PreviewRect(this)
    {
        Texture = _frames[0],
        Stretch = TextureRect.StretchMode.KeepAspectCentered,
        SetSize = new Vector2(_frames[0].Width, _frames[0].Height),
        HorizontalAlignment = Control.HAlignment.Left,
    };

    public void Dispose() => _texture.Dispose();

    private sealed class PreviewRect(AhelpThumbnail thumbnail) : TextureRect
    {
        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);
            if (!VisibleInTree || thumbnail._frames.Length == 1)
                return;
            var index = SelectFrame(thumbnail._frameEnds, thumbnail._loops,
                (thumbnail._timing.RealTime - thumbnail._started).TotalMilliseconds);
            var frame = thumbnail._frames[index];
            if (Texture != frame)
                Texture = frame;
        }
    }
}
