using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Serialization;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>One of a column's most frequent values: its bytes as the column stores them, its rows, and how many of those may be another's.</summary>
/// <param name="Value">The value's bytes: an integer or a float little-endian at the column's width, a string or a binary as its bytes.</param>
/// <param name="Rows">The rows that hold it, at most <paramref name="Error"/> too many.</param>
/// <param name="Error">0 when <paramref name="Rows"/> is exact.</param>
internal readonly record struct SheetValue(byte[] Value, long Rows, long Error);

/// <summary>
/// What an object says of one of its columns with no row read (<see cref="IndexKinds.ColumnSheet"/>): its rows and
/// nulls, its distinct values counted or estimated, a sketch of them that merges with another object's, its most
/// frequent values with their rows, and, for a column whose values repeat, a filter that proves a value absent from the
/// object. A region a column, which the object's table of contents names, so that a reader fetches it with no open.
/// </summary>
/// <remarks>
/// <para>
/// The distinct values are counted exactly while the column holds at most 2^20 of them, and estimated past that by a
/// HyperLogLog of 2^11 registers of six bits, 1 536 bytes, about 2.3 % of relative error, over the 64-bit XXH3 of each
/// value's bytes, the hash a Bloom filter takes them by. The frequent values are counted exactly as long as the
/// distinct ones are; a column whose distinct values pass a quarter of its rows keeps none, nor a filter: its values
/// hardly repeat, and an index of them would weigh as much as the column.
/// </para>
/// <para>
/// The filter is a binary fuse filter of 8-bit fingerprints over the distinct hashes: 1.125 bytes a value for a large
/// set, a false positive in 256. A sheet weighs about 4.5 KB plus that a distinct value.
/// </para>
/// </remarks>
internal sealed class ColumnSheet
{
    /// <summary>The version this library writes and reads.</summary>
    internal const byte Version = 1;

    /// <summary>The register index's bits.</summary>
    internal const int Precision = 11;

    /// <summary>The registers.</summary>
    internal const int Registers = 1 << Precision;

    /// <summary>The bytes of the registers written, six bits each.</summary>
    internal const int RegisterBytes = Registers * 6 / 8;

    /// <summary>The most frequent values a sheet keeps.</summary>
    internal const int TopValues = 128;

    private const byte ExactFlag = 1;
    private const byte FilterFlag = 2;

    private readonly byte[] _ranks;

    /// <param name="rows">The column's rows.</param>
    /// <param name="nulls">Its null rows.</param>
    /// <param name="distinct">Its distinct values, counted or estimated.</param>
    /// <param name="exact">Whether <paramref name="distinct"/> was counted.</param>
    /// <param name="ranks">Each register's rank, <see cref="Registers"/> of them, which the sheet takes.</param>
    /// <param name="top">The most frequent values, the most first, <see cref="TopValues"/> at most.</param>
    /// <param name="filter">The filter of the distinct values, or null.</param>
    internal ColumnSheet(long rows, long nulls, long distinct, bool exact, byte[] ranks, IReadOnlyList<SheetValue> top, BinaryFuse8? filter)
    {
        if (ranks.Length != Registers)
        {
            throw new ArgumentException($"A sheet holds {Registers} registers, not {ranks.Length}.", nameof(ranks));
        }

        Rows = rows;
        Nulls = nulls;
        Distinct = distinct;
        IsExact = exact;
        _ranks = ranks;
        Top = top;
        Filter = filter;
    }

    /// <summary>The column's rows.</summary>
    internal long Rows { get; }

    /// <summary>Its null rows.</summary>
    internal long Nulls { get; }

    /// <summary>Its distinct values, counted when <see cref="IsExact"/>, estimated otherwise.</summary>
    internal long Distinct { get; }

    /// <summary>Whether <see cref="Distinct"/> was counted.</summary>
    internal bool IsExact { get; }

    /// <summary>The most frequent values, the most first; none for a column whose values hardly repeat.</summary>
    internal IReadOnlyList<SheetValue> Top { get; }

    /// <summary>The filter of the distinct values, or null: a column whose values hardly repeat, or past 2^20 of them.</summary>
    internal BinaryFuse8? Filter { get; }

    /// <summary>Each register's rank, for a merge with another object's.</summary>
    internal ReadOnlySpan<byte> Ranks => _ranks;

    /// <summary>The registers' estimate of the distinct values, whatever <see cref="Distinct"/> holds.</summary>
    internal long Estimate()
    {
        Span<int> counts = stackalloc int[64 - Precision + 2];
        foreach (byte rank in _ranks)
        {
            counts[rank]++;
        }

        return (long)Math.Round(HyperLogLog.Estimate(counts, Precision));
    }

    /// <summary>
    /// Whether the column may hold the value whose hash is <paramref name="hash"/>: false is a proof that no row does;
    /// true without a filter.
    /// </summary>
    internal bool MayContain(ulong hash) => Filter is not { } filter || filter.MayContain(hash);

    /// <summary>
    /// Whether the column, of <paramref name="dtype"/>, may hold <paramref name="literal"/>: false is a proof that no row
    /// does; true when the literal has no exact form in the column's type, or the sheet no filter.
    /// </summary>
    internal bool MayContain(FilterLiteral literal, DType dtype)
    {
        if (Filter is null || !KeyLayout.TryOf(dtype, out KeyLayout layout))
        {
            return true;
        }

        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        if (!layout.TryEncode(literal, scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
        {
            return true;
        }

        return MayContain(SplitBlockBloom.Hash(key, BloomHash.XxHash3))
            || (hasOtherZero && MayContain(SplitBlockBloom.Hash(zero[..layout.Width], BloomHash.XxHash3)));
    }

    /// <summary>The sheet's bytes, which <see cref="From"/> reads.</summary>
    internal byte[] ToBytes()
    {
        int size = 2 + (3 * Varint.MaxLength64) + RegisterBytes + Varint.MaxLength64 + (Filter?.Bytes ?? 0);
        foreach (SheetValue value in Top)
        {
            size += (3 * Varint.MaxLength64) + value.Value.Length;
        }

        byte[] bytes = new byte[size];
        Span<byte> into = bytes;
        into[0] = Version;
        into[1] = (byte)((IsExact ? ExactFlag : 0) | (Filter is null ? 0 : FilterFlag));
        int at = 2;
        at += Varint.Write(into[at..], (ulong)Rows);
        at += Varint.Write(into[at..], (ulong)Nulls);
        at += Varint.Write(into[at..], (ulong)Distinct);

        // The registers, six bits each, four in three bytes.
        for (int r = 0; r < Registers; r += 4)
        {
            uint word = (uint)_ranks[r] | ((uint)_ranks[r + 1] << 6) | ((uint)_ranks[r + 2] << 12) | ((uint)_ranks[r + 3] << 18);
            into[at++] = (byte)word;
            into[at++] = (byte)(word >> 8);
            into[at++] = (byte)(word >> 16);
        }

        at += Varint.Write(into[at..], (ulong)Top.Count);
        foreach (SheetValue value in Top)
        {
            at += Varint.Write(into[at..], (ulong)value.Rows);
            at += Varint.Write(into[at..], (ulong)value.Error);
            at += Varint.Write(into[at..], (ulong)value.Value.Length);
            value.Value.CopyTo(into[at..]);
            at += value.Value.Length;
        }

        if (Filter is { } filter)
        {
            filter.Write(into[at..]);
            at += filter.Bytes;
        }

        return bytes.AsSpan(0, at).ToArray();
    }

    /// <summary>The sheet <see cref="ToBytes"/> wrote.</summary>
    /// <exception cref="VortexFormatException">The bytes are no sheet this library reads.</exception>
    internal static ColumnSheet From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != Version)
        {
            throw new VortexFormatException($"A column sheet of version {(bytes.Length > 0 ? bytes[0] : 0)}, where this library reads {Version}.");
        }

        byte flags = bytes[1];
        int at = 2;
        long rows = (long)Varint.Read64<Errors>(bytes, ref at);
        long nulls = (long)Varint.Read64<Errors>(bytes, ref at);
        long distinct = (long)Varint.Read64<Errors>(bytes, ref at);
        if (bytes.Length - at < RegisterBytes)
        {
            throw new VortexFormatException("A column sheet ends inside its registers.");
        }

        byte[] ranks = new byte[Registers];
        for (int r = 0; r < Registers; r += 4)
        {
            uint word = bytes[at] | ((uint)bytes[at + 1] << 8) | ((uint)bytes[at + 2] << 16);
            at += 3;
            ranks[r] = (byte)(word & 63);
            ranks[r + 1] = (byte)((word >> 6) & 63);
            ranks[r + 2] = (byte)((word >> 12) & 63);
            ranks[r + 3] = (byte)((word >> 18) & 63);
        }

        ulong count = Varint.Read64<Errors>(bytes, ref at);
        if (count > TopValues)
        {
            throw new VortexFormatException($"A column sheet keeps {count} frequent values, past {TopValues}.");
        }

        SheetValue[] top = new SheetValue[count];
        for (int i = 0; i < top.Length; i++)
        {
            long held = (long)Varint.Read64<Errors>(bytes, ref at);
            long error = (long)Varint.Read64<Errors>(bytes, ref at);
            ulong length = Varint.Read64<Errors>(bytes, ref at);
            if (length > (ulong)(bytes.Length - at))
            {
                throw new VortexFormatException("A column sheet ends inside a frequent value.");
            }

            top[i] = new SheetValue(bytes.Slice(at, (int)length).ToArray(), held, error);
            at += (int)length;
        }

        BinaryFuse8? filter = (flags & FilterFlag) != 0 ? BinaryFuse8.Read(bytes[at..], out _) : null;
        return new ColumnSheet(rows, nulls, distinct, (flags & ExactFlag) != 0, ranks, top, filter);
    }

    private readonly struct Errors : IVarintErrors
    {
        public static ulong Truncated(int position) => throw new VortexFormatException($"A column sheet ends inside a varint at {position}.");

        public static ulong Malformed(int position, byte value) => throw new VortexFormatException($"A column sheet holds a varint too wide at {position}.");
    }
}
