using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// A measure's histograms over the granules of a file whose rows follow a key (spec D, 2.2): each granule's values of the
/// measure in a log-linear sketch (<see cref="Aggregating.QuantileSketch"/>) of <see cref="Bits"/> mantissa bits, which a
/// quantile or a distribution merges over the granules it answers, within <c>1 / (2^(bits + 1) + 1)</c> of any value of a
/// bucket: 1/17, about 5.9 %, at 3 bits. Kept apart from the granules' statistics (<see cref="GranuleStats"/>), so that a
/// question of sums, which reads those, never reads these.
/// </summary>
/// <remarks>
/// Encoded as its version, its count of granules and the bits of its grid, then each granule's sketch, its length then its
/// bytes as <see cref="Aggregating.QuantileSketch.Encode"/> writes them. Read back into one array, each granule's sketch a
/// slice of it.
/// </remarks>
internal sealed class GranuleHistograms
{
    /// <summary>The encoding's version.</summary>
    internal const int Version = 1;

    /// <summary>
    /// The mantissa bits of the grid a writer keeps a granule's histogram in: within 0.77 % of any value of a bucket, the grid a
    /// quantile asks by default. A granule of the design's requests keeps 575 bytes of durations, 112 at three bits, 0.35 % of
    /// the table, where the rows of the durations take 3.3 bytes a row. An object written at three bits keeps answering the
    /// coarser quantiles.
    /// </summary>
    internal const int WrittenBits = 6;

    // The granules' sketches, one after another; where each one starts, and where the last one ends.
    private readonly byte[] _sketches;
    private readonly int[] _starts;

    private GranuleHistograms(int bits, byte[] sketches, int[] starts)
    {
        Bits = bits;
        _sketches = sketches;
        _starts = starts;
    }

    /// <summary>The mantissa bits of the histograms' grid: a quantile that asks as many or fewer merges them.</summary>
    internal int Bits { get; }

    /// <summary>The granules.</summary>
    internal int Count => _starts.Length - 1;

    /// <summary>The bytes the histograms hold in memory.</summary>
    internal long Bytes => _sketches.Length + (_starts.Length * (long)sizeof(int));

    /// <summary>Granule <paramref name="granule"/>'s histogram, as <see cref="Aggregating.QuantileSketch.Encode"/> wrote it.</summary>
    internal ReadOnlyMemory<byte> HistogramOf(int granule) => _sketches.AsMemory(_starts[granule], _starts[granule + 1] - _starts[granule]);

    /// <summary>Writes the header of the histograms of <paramref name="granules"/> granules, their sketches following.</summary>
    internal static void WriteHeader(ref ProtoWriter writer, int granules)
    {
        writer.WriteVarint(Version);
        writer.WriteVarint((ulong)granules);
        writer.WriteVarint(WrittenBits);
    }

    /// <summary>The header <see cref="WriteHeader(ref ProtoWriter, int)"/> writes, into <paramref name="into"/>, 30 bytes at most: its length.</summary>
    internal static int WriteHeader(Span<byte> into, int granules)
    {
        int at = Serialization.Varint.Write(into, Version);
        at += Serialization.Varint.Write(into[at..], (ulong)granules);
        return at + Serialization.Varint.Write(into[at..], WrittenBits);
    }

    /// <summary>The histograms <paramref name="bytes"/> encode; null when they do not decode.</summary>
    internal static GranuleHistograms? From(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            ulong bits = reader.ReadVarint();
            if (count > (ulong)bytes.Length || bits is < 1 or > Aggregating.QuantileSketch.MaxBits)
            {
                return null;
            }

            // Their lengths first, on a copy of the reader, then their bytes into one array of those.
            int granules = (int)count;
            int[] starts = new int[granules + 1];
            ProtoReader lengths = reader;
            for (int g = 0; g < granules; g++)
            {
                starts[g + 1] = checked(starts[g] + lengths.ReadLengthDelimited().Length);
            }

            byte[] sketches = new byte[starts[granules]];
            for (int g = 0; g < granules; g++)
            {
                reader.ReadLengthDelimited().CopyTo(sketches.AsSpan(starts[g]));
            }

            return reader.End ? new GranuleHistograms((int)bits, sketches, starts) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
