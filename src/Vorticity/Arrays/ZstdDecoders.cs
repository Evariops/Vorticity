using System.Threading;
using Vorticity.Zstd;

namespace Vorticity.Arrays;

/// <summary>
/// The zstd decompressors scan contexts decompress their frames with, kept from one context to the
/// next: a decompressor's tables and buffers are 160 KB, and 1.4 MB once it has decoded a frame of
/// several blocks, which a scan built once per context, so once per scan and per lane, where the
/// next scan needed the same.
/// </summary>
/// <remarks>
/// A decompressor decodes whole frames, one per call, each with the dictionary it is given, so
/// nothing of one scan reaches the next. The bound is what a few scans at once hold, a lane each; a
/// decompressor past it is left to the collector.
/// </remarks>
internal static class ZstdDecoders
{
    private const int Capacity = 16;

    private static readonly ZstdDecompressor?[] Decoders = new ZstdDecompressor?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A decompressor an earlier context gave back, or a new one when none waits.</summary>
    internal static ZstdDecompressor Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                ZstdDecompressor decoder = Decoders[--_count]!;
                Decoders[_count] = null;
                return decoder;
            }
        }

        return new ZstdDecompressor();
    }

    /// <summary>Keeps <paramref name="decoder"/> for a later context, or lets it go past the bound.</summary>
    internal static void Return(ZstdDecompressor decoder)
    {
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Decoders[_count++] = decoder;
            }
        }
    }
}
