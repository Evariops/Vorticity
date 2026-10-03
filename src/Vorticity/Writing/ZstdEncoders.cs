using System.Threading;
using Vorticity.Zstd;

namespace Vorticity.Writing;

/// <summary>
/// The zstd compressors writers compress their trials with, kept from one writer to the next: a
/// compressor's tables and buffers are close to two megabytes at the default level, which every
/// file written built at its first trial and dropped with its writer, where the next file needed
/// the same.
/// </summary>
/// <remarks>
/// A compressor writes whole frames, one per call, each the same bytes whatever it compressed
/// before, so nothing of one file reaches the next. The bound is what a few writers at once hold,
/// one each, or one writer compressing a column's frames on every processor, one each; a
/// compressor past it is left to the collector, since each one kept is two megabytes held.
/// </remarks>
internal static class ZstdEncoders
{
    private static readonly int Capacity = System.Math.Max(8, System.Environment.ProcessorCount + 1);

    private static readonly ZstdCompressor?[] Encoders = new ZstdCompressor?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A compressor an earlier writer gave back, or a new one at the default level when none waits.</summary>
    internal static ZstdCompressor Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                ZstdCompressor encoder = Encoders[--_count]!;
                Encoders[_count] = null;
                return encoder;
            }
        }

        return new ZstdCompressor();
    }

    /// <summary>Keeps <paramref name="encoder"/> for a later writer, or lets it go past the bound.</summary>
    internal static void Return(ZstdCompressor encoder)
    {
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Encoders[_count++] = encoder;
            }
        }
    }
}
