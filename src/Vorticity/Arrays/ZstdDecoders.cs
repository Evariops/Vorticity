using System.IO.Compression;
using System.Threading;

namespace Vorticity.Arrays;

/// <summary>
/// The zstd decoders scan contexts decompress their frames with, kept from one context to the
/// next: a decoder's native state is about 96 KB, which a scan built and freed once per context,
/// so once per scan and per lane, where the next scan needed the same.
/// </summary>
/// <remarks>
/// A decoder comes back reset, and every use resets it again before a frame, so nothing of one
/// scan reaches the next. The bound is what a few scans at once hold, a lane each; a decoder past
/// it is disposed.
/// </remarks>
internal static class ZstdDecoders
{
    private const int Capacity = 16;

    private static readonly ZstandardDecoder?[] Decoders = new ZstandardDecoder?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A decoder an earlier context gave back, or a new one when none waits.</summary>
    internal static ZstandardDecoder Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                ZstandardDecoder decoder = Decoders[--_count]!;
                Decoders[_count] = null;
                return decoder;
            }
        }

        return new ZstandardDecoder();
    }

    /// <summary>Resets <paramref name="decoder"/> and keeps it for a later context, or disposes it past the bound.</summary>
    internal static void Return(ZstandardDecoder decoder)
    {
        decoder.Reset();
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Decoders[_count++] = decoder;
                return;
            }
        }

        decoder.Dispose();
    }
}
