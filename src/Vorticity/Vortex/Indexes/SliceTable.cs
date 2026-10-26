using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The slices of a file whose rows lie in the order of a key: per slice its rows and its first key, in order, covering
/// every row once, and the last key of the last slice. An entry of the index directory on the key's first column,
/// <see cref="IndexKinds.Slices"/>, whose options hold the table.
/// </summary>
/// <remarks>
/// <para>
/// The slices follow each other: each one's rows follow the one before's, and its first key is at or past the one
/// before's. The table is encoded behind a version byte as the slice count, per slice its rows and its first key, as the
/// bytes it shares with the first key before it and the rest, and the last key of the last slice the same way.
/// </para>
/// <para>
/// Like every index it is a hint: a table that does not decode, or whose rows are not the file's,
/// is not read, and the file reads without it.
/// </para>
/// </remarks>
internal sealed class SliceTable
{
    // A table bounded by keys. Versions 4 and 5 bounded the slices by a time, retired with the datasets cut by periods;
    // a reader takes them for no table.
    private const byte Keyed = 6;

    // The first key of each slice, and the last key of the last one, row-encoded.
    private readonly byte[][] _lows;
    private readonly byte[] _high;

    private SliceTable(RowRange[] slices, byte[][] lows, byte[] high)
    {
        Slices = slices;
        _lows = lows;
        _high = high;
    }

    /// <summary>
    /// A table of the slices of a file whose rows lie in the order of a key: each slice's <paramref name="rows"/>, its first
    /// key in <paramref name="lows"/>, and the last key of the last one, <paramref name="high"/>, row-encoded as the key
    /// orders them, so that a slice's keys lie from its first to the next slice's, or to <paramref name="high"/>.
    /// </summary>
    /// <exception cref="ArgumentException">There is no slice, a slice holds no row, or the keys are not in order.</exception>
    internal static SliceTable ByKeys(IReadOnlyList<long> rows, IReadOnlyList<byte[]> lows, byte[] high)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(high);
        if (rows.Count == 0 || rows.Count != lows.Count)
        {
            throw new ArgumentException($"{rows.Count} slice(s) of rows and {lows.Count} first key(s): a key table holds a first key a slice, and a slice at least.", nameof(rows));
        }

        RowRange[] slices = new RowRange[rows.Count];
        long row = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            if (rows[i] <= 0)
            {
                throw new ArgumentException($"Slice {i} holds {rows[i]} rows: a slice holds rows.", nameof(rows));
            }

            slices[i] = new RowRange(row, row + rows[i]);
            row += rows[i];
        }

        byte[][] held = [.. lows];
        return KeyProblem(held, high) is { } problem
            ? throw new ArgumentException(problem, nameof(lows))
            : new SliceTable(slices, held, high);
    }

    /// <summary>The slices' rows, in order.</summary>
    internal IReadOnlyList<RowRange> Slices { get; }

    /// <summary>The rows the slices cover.</summary>
    internal long Rows => Slices[^1].End;

    /// <summary>The first key of slice <paramref name="slice"/>, row-encoded: no key of the slice is below it.</summary>
    internal ReadOnlySpan<byte> LowOf(int slice) => _lows[slice];

    /// <summary>
    /// A key at or above every key of slice <paramref name="slice"/>, row-encoded: the next slice's first, or the last key
    /// of the last slice.
    /// </summary>
    internal ReadOnlySpan<byte> CeilingOf(int slice) => slice + 1 < _lows.Length ? _lows[slice + 1] : _high;

    /// <summary>Why a table's keys are not in order, or null when they are.</summary>
    private static string? KeyProblem(byte[][] lows, byte[] high)
    {
        for (int i = 1; i < lows.Length; i++)
        {
            if (lows[i].AsSpan().SequenceCompareTo(lows[i - 1]) < 0)
            {
                return $"Slice {i} starts below the slice before it: the slices follow the key.";
            }
        }

        return high.AsSpan().SequenceCompareTo(lows[^1]) < 0 ? "The last key lies below the last slice's first: the slices follow the key." : null;
    }

    /// <summary>The table's bytes, the options of its directory entry.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Keyed);
            writer.WriteVarint((ulong)Slices.Count);
            byte[] before = [];
            for (int i = 0; i < Slices.Count; i++)
            {
                writer.WriteVarint((ulong)Slices[i].Length);
                WriteKey(ref writer, before, _lows[i]);
                before = _lows[i];
            }

            WriteKey(ref writer, before, _high);
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

    /// <summary>The table <paramref name="bytes"/> encode, or null when they do not decode as one.</summary>
    internal static SliceTable? FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            return reader.ReadVarint() == Keyed ? KeysFrom(ref reader, bytes.Length) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A table's slices and keys, past its version byte; null when they do not decode as one.</summary>
    private static SliceTable? KeysFrom(ref ProtoReader reader, int bytes)
    {
        ulong count = reader.ReadVarint();
        if (count == 0 || count > (ulong)bytes)
        {
            return null;
        }

        RowRange[] slices = new RowRange[(int)count];
        byte[][] lows = new byte[slices.Length][];
        byte[] before = [];
        long row = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            long rows = (long)reader.ReadVarint();
            if (rows <= 0)
            {
                return null;
            }

            long end = checked(row + rows);
            slices[i] = new RowRange(row, end);
            row = end;
            if (ReadKey(ref reader, before) is not { } low)
            {
                return null;
            }

            lows[i] = before = low;
        }

        return ReadKey(ref reader, before) is { } high && reader.End && KeyProblem(lows, high) is null
            ? new SliceTable(slices, lows, high)
            : null;
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

    /// <summary>
    /// The slice table of <paramref name="file"/>, read from its index directory: null when it has
    /// none, or one that does not decode or does not cover the file's rows.
    /// </summary>
    internal static async ValueTask<SliceTable?> ReadAsync(VortexFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind == IndexKinds.Slices && FromBytes(entry.Options) is { } table && table.Rows == file.RowCount)
            {
                return table;
            }
        }

        return null;
    }
}
