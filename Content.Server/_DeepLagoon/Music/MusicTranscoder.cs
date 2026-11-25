using System.IO;
using NLayer;
using Robust.Shared.Log;

namespace Content.Server._DeepLagoon.Music;

public static class MusicTranscoder
{
    public static DecodedTrack? ToWav(byte[] compressed, ISawmill sawmill, long maxPcmBytes = 256L * 1024 * 1024)
    {
        try
        {
            using var input = new MemoryStream(compressed, false);
            using var mpeg = new MpegFile(input);

            var channels = mpeg.Channels;
            var sampleRate = mpeg.SampleRate;
            if (channels <= 0 || sampleRate <= 0)
            {
                sawmill.Warning("Transcode failed: unusable stream header");
                return null;
            }

            var bytesPerSampleFrame = channels * 2;
            var maxFrames = maxPcmBytes / bytesPerSampleFrame;

            using var pcm = new MemoryStream();
            var buffer = new float[Math.Max(sampleRate / 4, 4096)];
            long frames = 0;
            while (true)
            {
                var read = mpeg.ReadSamples(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                frames += read / channels;
                if (frames > maxFrames)
                {
                    sawmill.Warning("Transcode aborted: decoded audio exceeds {Limit} MiB",
                        maxPcmBytes / (1024 * 1024));
                    return null;
                }

                for (var i = 0; i < read; i++)
                {
                    var value = buffer[i];
                    if (value > 1f) value = 1f;
                    else if (value < -1f) value = -1f;

                    var sample = (short) (value * short.MaxValue);
                    pcm.WriteByte((byte) (sample & 0xFF));
                    pcm.WriteByte((byte) ((sample >> 8) & 0xFF));
                }
            }

            if (pcm.Length == 0)
            {
                sawmill.Warning("Transcode produced no samples");
                return null;
            }

            return new DecodedTrack(pcm.ToArray(), sampleRate, channels);
        }
        catch (Exception e)
        {
            sawmill.Warning("Transcode failed: {Error}", e.GetType().Name);
            return null;
        }
    }

    public static byte[] WrapWav(byte[] pcm, int sampleRate, int channels)
    {
        const short bitsPerSample = 16;
        var blockAlign = (short) (channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        var wav = new byte[44 + pcm.Length];

        "RIFF"u8.CopyTo(wav.AsSpan(0));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm.Length);
        "WAVE"u8.CopyTo(wav.AsSpan(8));

        "fmt "u8.CopyTo(wav.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short) channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), byteRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), blockAlign);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), bitsPerSample);

        "data"u8.CopyTo(wav.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav, 44);

        return wav;
    }
}
