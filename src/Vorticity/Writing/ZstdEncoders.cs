using System.IO.Compression;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// The zstd encoders writers compress their trials with, kept from one writer to the next: an
/// encoder's native context is close to a megabyte, which every file written built at its first
/// trial and freed with its writer, where the next file needed the same.
/// </summary>
/// <remarks>
/// An encoder comes back reset, and every trial resets it again before it compresses, so nothing
/// of one file reaches the next. The bound is what a few writers at once hold, one each, or one
/// writer compressing a column's frames on every processor, one each; an encoder past it is
/// disposed, since each one kept is a megabyte held.
/// </remarks>
internal static class ZstdEncoders
{
    private static readonly int Capacity = System.Math.Max(8, System.Environment.ProcessorCount + 1);

    private static readonly ZstandardEncoder?[] Encoders = new ZstandardEncoder?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>An encoder an earlier writer gave back, or a new one when none waits.</summary>
    internal static ZstandardEncoder Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                ZstandardEncoder encoder = Encoders[--_count]!;
                Encoders[_count] = null;
                return encoder;
            }
        }

        return new ZstandardEncoder();
    }

    /// <summary>Resets <paramref name="encoder"/> and keeps it for a later writer, or disposes it past the bound.</summary>
    internal static void Return(ZstandardEncoder encoder)
    {
        encoder.Reset();
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Encoders[_count++] = encoder;
                return;
            }
        }

        encoder.Dispose();
    }
}
