using System.Threading;
using ManagedCompressions.Zstd;

namespace Vorticity.Writing;

/// <summary>
/// The zstd compressors writers compress their trials with, kept from one writer to the next: a
/// compressor's tables and buffers are close to two megabytes at the default level, which every
/// file written built at its first trial and dropped with its writer, where the next file needed
/// the same.
/// </summary>
/// <remarks>
/// A compressor writes whole frames, one per call, each the same bytes whatever it compressed
/// before, so nothing of one file reaches the next. Its tables are sized for its level, so a rental
/// takes one of the level asked for, the last given back first, and makes one when none waits. The
/// bound is what a few writers at once hold, one each, or one writer compressing a column's frames
/// on every processor, one each; past it the compressor given back longest ago is left to the
/// collector, so that the levels the writers of the moment use are the ones kept.
/// </remarks>
internal static class ZstdEncoders
{
    private static readonly int Capacity = System.Math.Max(8, System.Environment.ProcessorCount + 1);

    private static readonly ZstdCompressor?[] Encoders = new ZstdCompressor?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A compressor at the default level an earlier writer gave back, or a new one when none waits.</summary>
    internal static ZstdCompressor Rent() => Rent(ZstdCompressor.DefaultLevel);

    /// <summary>A compressor at <paramref name="level"/> an earlier writer gave back, or a new one when none waits.</summary>
    /// <param name="level">The level; 0 is the default one.</param>
    internal static ZstdCompressor Rent(int level)
    {
        int wanted = level == 0 ? ZstdCompressor.DefaultLevel : level;
        lock (Gate)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ZstdCompressor encoder = Encoders[i]!;
                if (encoder.Level == wanted)
                {
                    // Closed up rather than swapped, so that the kept stay in the order they came back.
                    _count--;
                    System.Array.Copy(Encoders, i + 1, Encoders, i, _count - i);
                    Encoders[_count] = null;
                    return encoder;
                }
            }
        }

        return new ZstdCompressor(wanted);
    }

    /// <summary>Keeps <paramref name="encoder"/> for a later writer, in place of the one kept longest when full.</summary>
    internal static void Return(ZstdCompressor encoder)
    {
        lock (Gate)
        {
            if (_count == Capacity)
            {
                System.Array.Copy(Encoders, 1, Encoders, 0, Capacity - 1);
                _count--;
            }

            Encoders[_count++] = encoder;
        }
    }
}
