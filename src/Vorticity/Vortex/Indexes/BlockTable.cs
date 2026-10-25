using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The blocks of a file whose rows follow a key (<see cref="IndexKinds.BlockStats"/>): runs of rows of one value of the
/// key's first column, each its rows and the first and the last key it holds, which the statistics of each column of
/// each block extend. The blocks cover the file's rows once, in order, and their keys follow the key.
/// </summary>
/// <remarks>
/// <para>
/// A block ends where the key's first column changes once it holds <see cref="LeastRows"/> rows, and at
/// <see cref="MostRows"/> rows whatever its rows hold: a first column whose runs are shorter, an identifier, gives blocks
/// of several values rather than a block a row. A block is a range of rows, independent of the file's chunks and zones.
/// </para>
/// <para>
/// Encoded as its version, its count of blocks, then each block's rows and its first and last keys, each key as the bytes
/// it shares with the one before it, a count, and the rest.
/// </para>
/// </remarks>
internal sealed class BlockTable
{
    /// <summary>The rows a block holds at most.</summary>
    internal const int MostRows = 65_536;

    /// <summary>The rows a block holds before a change of the key's first column ends it: a sixteenth of <see cref="MostRows"/>.</summary>
    internal const int LeastRows = MostRows / 16;

    private const byte Version = 1;

    private readonly long[] _starts;

    private BlockTable(long[] rows, byte[][] lows, byte[][] highs)
    {
        Rows = rows;
        Lows = lows;
        Highs = highs;
        _starts = new long[rows.Length];
        for (int b = 1; b < rows.Length; b++)
        {
            _starts[b] = _starts[b - 1] + rows[b - 1];
        }
    }

    /// <summary>The blocks, in the order of the file's rows.</summary>
    internal int Count => Rows.Length;

    /// <summary>Each block's rows.</summary>
    internal long[] Rows { get; }

    /// <summary>Each block's first key, as the clustering key encodes it.</summary>
    internal byte[][] Lows { get; }

    /// <summary>Each block's last key, as the clustering key encodes it.</summary>
    internal byte[][] Highs { get; }

    /// <summary>The first row of block <paramref name="block"/> in the file.</summary>
    internal long FirstRow(int block) => _starts[block];

    /// <summary>The rows the blocks cover.</summary>
    internal long TotalRows => Rows.Length == 0 ? 0 : _starts[^1] + Rows[^1];

    /// <summary>The bytes the table holds in memory: its keys, and a few words a block.</summary>
    internal long Bytes
    {
        get
        {
            long bytes = (long)Rows.Length * 3 * sizeof(long);
            for (int b = 0; b < Rows.Length; b++)
            {
                bytes += Lows[b].Length + Highs[b].Length;
            }

            return bytes;
        }
    }

    /// <summary>A table of <paramref name="rows"/> a block, each from its first key to its last.</summary>
    /// <exception cref="ArgumentException">The lists differ in length, a block holds no row, or the keys do not follow one another.</exception>
    internal static BlockTable Of(IReadOnlyList<long> rows, IReadOnlyList<byte[]> lows, IReadOnlyList<byte[]> highs)
    {
        BlockTable table = new BlockTable([.. rows], [.. lows], [.. highs]);
        return table.Problem() is { } problem ? throw new ArgumentException(problem, nameof(rows)) : table;
    }

    /// <summary>What makes the table no table of blocks, or null.</summary>
    private string? Problem()
    {
        if (Rows.Length != Lows.Length || Rows.Length != Highs.Length)
        {
            return $"{Rows.Length} blocks' rows, {Lows.Length} first keys and {Highs.Length} last keys: a block has one of each.";
        }

        for (int b = 0; b < Rows.Length; b++)
        {
            if (Rows[b] <= 0)
            {
                return $"Block {b} holds {Rows[b]} rows: a block holds rows.";
            }

            if (Highs[b].AsSpan().SequenceCompareTo(Lows[b]) < 0)
            {
                return $"Block {b} ends below its first key: a block's keys follow the key.";
            }

            if (b > 0 && Lows[b].AsSpan().SequenceCompareTo(Highs[b - 1]) < 0)
            {
                return $"Block {b} starts below the last key of the block before it: the blocks follow the key.";
            }
        }

        return null;
    }

    /// <summary>The table's bytes, the payload of its region.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)Rows.Length);
            byte[] before = [];
            for (int b = 0; b < Rows.Length; b++)
            {
                writer.WriteVarint((ulong)Rows[b]);
                WriteKey(ref writer, before, Lows[b]);
                WriteKey(ref writer, Lows[b], Highs[b]);
                before = Highs[b];
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>A key as the bytes it shares with <paramref name="before"/>, a count, and the rest, its length first.</summary>
    private static void WriteKey(ref ProtoWriter writer, ReadOnlySpan<byte> before, ReadOnlySpan<byte> key)
    {
        int shared = key.CommonPrefixLength(before);
        writer.WriteVarint((ulong)shared);
        writer.WriteVarint((ulong)(key.Length - shared));
        writer.WriteRawBytes(key[shared..]);
    }

    /// <summary>The table <paramref name="bytes"/> encode, or null when they do not decode as one of this version.</summary>
    internal static BlockTable? From(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            long[] rows = new long[(int)count];
            byte[][] lows = new byte[rows.Length][];
            byte[][] highs = new byte[rows.Length][];
            byte[] before = [];
            for (int b = 0; b < rows.Length; b++)
            {
                rows[b] = checked((long)reader.ReadVarint());
                if (ReadKey(ref reader, before) is not { } low || ReadKey(ref reader, low) is not { } high)
                {
                    return null;
                }

                lows[b] = low;
                highs[b] = before = high;
            }

            BlockTable table = new BlockTable(rows, lows, highs);
            return reader.End && table.Problem() is null ? table : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A key written by <see cref="WriteKey"/> after <paramref name="before"/>; null when its counts do not fit.</summary>
    private static byte[]? ReadKey(ref ProtoReader reader, byte[] before)
    {
        ulong shared = reader.ReadVarint();
        if (shared > (ulong)before.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> tail = reader.ReadLengthDelimited();
        byte[] key = new byte[(int)shared + tail.Length];
        before.AsSpan(0, (int)shared).CopyTo(key);
        tail.CopyTo(key.AsSpan((int)shared));
        return key;
    }
}
