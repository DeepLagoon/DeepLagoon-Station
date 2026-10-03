using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>One reusable black/alpha texture per grid, with nearest sampling and change-only uploads.</summary>
internal sealed class AmbientOcclusionMask : IDisposable
{
    internal const int Steps = 16;
    private static readonly float[][] WallSamples = MakeWallSamples();
    private Rgba32[] _previous = Array.Empty<Rgba32>();
    private Rgba32[] _base = Array.Empty<Rgba32>();
    public int UploadedBytes { get; private set; }
    public Rgba32[] Pixels { get; private set; } = Array.Empty<Rgba32>();
    public Vector2i Capacity { get; private set; }
    public OwnedTexture? Texture { get; private set; }
    public uint LastUsedFrame;
    public readonly AmbientOcclusionSceneCache Scene = new();
    private bool _uploaded;

    public void Prepare(IClyde clyde, int width, int height)
    {
        if (Texture == null || width > Capacity.X || height > Capacity.Y)
        {
            Texture?.Dispose();
            Capacity = new Vector2i(Grow(width), Grow(height));
            Pixels = new Rgba32[Capacity.X * Capacity.Y];
            _previous = new Rgba32[Pixels.Length];
            _base = new Rgba32[Pixels.Length];
            Texture = clyde.CreateBlankTexture<Rgba32>(Capacity, "ambient-occlusion-mask",
                new TextureLoadParameters { SampleParameters = new TextureSampleParameters { Filter = false } });
            _uploaded = false;
        }
        Array.Clear(Pixels);
    }

    private static int Grow(int value)
    {
        var result = 64;
        while (result < value) result *= 2;
        return result;
    }

    public bool Upload()
    {
        UploadedBytes = 0;
        if (_uploaded && Pixels.AsSpan().SequenceEqual(_previous))
            return false;
        // Upload only changed rows, including the old footprint when a contact moves/disappears.
        var first = 0;
        var last = Capacity.Y - 1;
        if (_uploaded)
        {
            while (first <= last && Pixels.AsSpan(first * Capacity.X, Capacity.X)
                       .SequenceEqual(_previous.AsSpan(first * Capacity.X, Capacity.X))) first++;
            while (last > first && Pixels.AsSpan(last * Capacity.X, Capacity.X)
                       .SequenceEqual(_previous.AsSpan(last * Capacity.X, Capacity.X))) last--;
        }
        var rows = last - first + 1;
        Texture!.SetSubImage<Rgba32>(new Vector2i(0, first), new Vector2i(Capacity.X, rows),
            Pixels.AsSpan(first * Capacity.X, rows * Capacity.X));
        UploadedBytes = rows * Capacity.X * 4;
        (Pixels, _previous) = (_previous, Pixels);
        _uploaded = true;
        return true;
    }

    public void StoreBase() => Pixels.CopyTo(_base, 0);
    public void RestoreBase() => _base.CopyTo(Pixels, 0);

    /// <summary>Input rows are world bottom-to-top; texture memory rows are top-to-bottom.</summary>
    internal static void ComposeTile(Span<Rgba32> pixels, int stride, int height, int x, int y,
        int wallMask, float[]? contacts, float intensity, bool preserve = false)
    {
        var walls = WallSamples[wallMask];
        for (var row = 0; row < Steps; row++)
        {
            var offset = (height - 1 - y - row) * stride + x;
            for (var column = 0; column < Steps; column++)
            {
                var index = row * Steps + column;
                var alpha = Math.Max(walls[index], contacts?[index] ?? 0) * intensity;
                var quantized = (byte) Math.Clamp((int) MathF.Round(alpha * 255), 0, 255);
                pixels[offset + column].A = preserve ? Math.Max(pixels[offset + column].A, quantized) : quantized;
            }
        }
    }

    private static float[][] MakeWallSamples()
    {
        var result = new float[16][];
        for (var mask = 0; mask < 16; mask++)
        {
            var samples = result[mask] = new float[Steps * Steps];
            for (var y = 0; y < Steps; y++)
            for (var x = 0; x < Steps; x++)
            {
                var horizontal = Math.Max((mask & 1) != 0 ? Falloff(x) : 0, (mask & 2) != 0 ? Falloff(Steps - 1 - x) : 0);
                var vertical = Math.Max((mask & 4) != 0 ? Falloff(y) : 0, (mask & 8) != 0 ? Falloff(Steps - 1 - y) : 0);
                samples[y * Steps + x] = Math.Min(0.22f, 0.22f * (Math.Max(horizontal, vertical) + 0.35f * Math.Min(horizontal, vertical)));
            }
        }
        return result;

        static float Falloff(int distance)
        {
            var value = Math.Max(0, (4 - distance - 0.5f) / 4);
            return value * value;
        }
    }

    public void Dispose() => Texture?.Dispose();
}
