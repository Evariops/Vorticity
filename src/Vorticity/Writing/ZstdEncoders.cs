using System.Threading;
using ManagedCompressions.Zstd;
using Vorticity.Buffers;

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
/// collector, so that the levels the writers of the moment use are the ones kept. The registry of
/// kept memory lets go of all of them when no writer rented one since it last asked, and when a
/// reservation needs the room (<see cref="KeptWhole"/>): kept outside it, up to 130 MB on 64
/// processors stayed for the process's life.
/// </remarks>
internal static class ZstdEncoders
{
    private static readonly int Capacity = System.Math.Max(8, System.Environment.ProcessorCount + 1);

    /// <summary>What a compressor holds at the default level, close to two megabytes.</summary>
    private const long EncoderBytes = 2L << 20;

    private static readonly ZstdCompressor?[] Encoders = new ZstdCompressor?[Capacity];
    private static int _count;

    private static readonly KeptWhole Kept = new KeptWhole("encoders", static () => Volatile.Read(ref _count) * EncoderBytes, LetGo);

    /// <summary>The compressors kept, as the registry of kept memory holds them.</summary>
    internal static IHeldMemory Held => Kept;

    /// <summary>A compressor at the default level an earlier writer gave back, or a new one when none waits.</summary>
    internal static ZstdCompressor Rent() => Rent(ZstdCompressor.DefaultLevel);

    /// <summary>A compressor at <paramref name="level"/> an earlier writer gave back, or a new one when none waits.</summary>
    /// <param name="level">The level; 0 is the default one.</param>
    internal static ZstdCompressor Rent(int level)
    {
        int wanted = level == 0 ? ZstdCompressor.DefaultLevel : level;
        lock (Kept.Gate)
        {
            Kept.Took();
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
        lock (Kept.Gate)
        {
            if (_count == Capacity)
            {
                System.Array.Copy(Encoders, 1, Encoders, 0, Capacity - 1);
                _count--;
            }

            Encoders[_count++] = encoder;
        }
    }

    // Lets go of every compressor kept, under the gate.
    private static void LetGo()
    {
        System.Array.Clear(Encoders, 0, _count);
        _count = 0;
    }
}
