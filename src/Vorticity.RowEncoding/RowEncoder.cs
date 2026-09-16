// The two-pass driver, and the whole public surface of the package.
//
// ONE SIZING PASS, ONE WRITE PASS. The sizing pass is not a dry run of the write pass: fixed-width
// columns contribute a compile-time constant that is added to every row once at the end, so only
// variable-width columns are actually walked. A schema of nothing but integers therefore sizes in
// O(columns), not O(columns x rows).
//
// Synchronous on purpose. The library's async-only rule covers the I/O and scan surface; this is
// pure CPU work over in-memory arrays and there is nothing to await.
//
// Transcribed from vortex-row (`encoder.rs`, `size.rs`, `encode.rs`) at 0.86.1.
using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;

namespace Vorticity.RowEncoding;

/// <summary>
/// Encodes columns into byte-sortable row keys: <c>memcmp(encode(a), encode(b))</c> has the sign
/// of the tuple comparison of <c>a</c> and <c>b</c> under the given per-column
/// <see cref="RowSortField"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bytes are self-describing in no way at all.</b> They carry no type tags, no field names
/// and no sort options, so two keys are comparable only when they came from the same schema with
/// the same options - and a key means nothing without the schema that produced it.
/// </para>
/// <para>
/// <b>The format is experimental.</b> Upstream reserves the right to change the byte layout
/// between Vortex releases, which is why this ships as a <c>0.x</c> package separate from the
/// library's <c>1.x</c> core (docs/09-contracts.md §3). Do not persist these bytes in a durable
/// index. <see cref="VortexVersion"/> names the release this encoder follows.
/// </para>
/// <para>
/// Supported: <c>Null</c>, <c>Bool</c>, every primitive, <c>Decimal</c> up to 128 bits,
/// <c>Utf8</c>, <c>Binary</c>, <c>Struct</c> and <c>FixedSizeList</c>, nested arbitrarily.
/// Rejected with <see cref="VortexUnsupportedException"/>: variable-size <c>List</c>, <c>Map</c>,
/// <c>Variant</c>, <c>Union</c>, <c>Extension</c> and 256-bit <c>Decimal</c> - the format defines
/// no ordering for them and inventing one would put our bytes at odds with every other
/// implementation. Extension being rejected means timestamps and dates cannot be row-encoded
/// directly; normalize such a column to its storage type first.
/// </para>
/// <para>
/// NaNs are ordered by their raw bit pattern and are NOT canonicalized, so two NaNs with different
/// payloads produce different keys.
/// </para>
/// </remarks>
public static partial class RowEncoder
{
    /// <summary>
    /// The Vortex release whose <c>vortex-row</c> byte layout this encoder reproduces. The pair
    /// (package version, this string) is what a consumer actually needs in order to know whether
    /// two sets of keys are comparable.
    /// </summary>
    public const string VortexVersion = "0.86.1";

    /// <summary>Encodes the fields of a batch's root struct, one column per field.</summary>
    /// <param name="batch">The batch to encode; its root must be a struct.</param>
    /// <param name="fields">One entry per root field, in schema order.</param>
    /// <returns>The row keys. The caller disposes them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="ArgumentException">The batch is not tabular, or the counts disagree.</exception>
    /// <exception cref="VortexUnsupportedException">A column's dtype has no defined ordering.</exception>
    public static RowKeys Encode(RecordBatch batch, ReadOnlySpan<RowSortField> fields)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!batch.IsTabular)
        {
            throw new ArgumentException(
                "Only a batch whose root is a struct has fields to encode as columns. Encode the " +
                "root itself with the CanonicalArena overload.",
                nameof(batch));
        }

        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        int count = root.FieldCount;
        int[] columns = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            for (int i = 0; i < count; i++)
            {
                columns[i] = root.GetFieldIndex(i);
            }

            return Encode(arena, columns.AsSpan(0, count), fields);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(columns);
        }
    }

    /// <summary>Encodes columns into pooled row keys.</summary>
    /// <param name="arena">The arena holding the columns.</param>
    /// <param name="columns">One canonical node index per column, in key order.</param>
    /// <param name="fields">One entry per column, in the same order.</param>
    /// <returns>The row keys. The caller disposes them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="ArgumentException">There are no columns, or the counts disagree.</exception>
    /// <exception cref="VortexUnsupportedException">A column's dtype has no defined ordering.</exception>
    /// <exception cref="VortexFormatException">A column contradicts its dtype, or the output exceeds 2 GiB.</exception>
    public static RowKeys Encode(
        CanonicalArena arena, ReadOnlySpan<int> columns, ReadOnlySpan<RowSortField> fields)
    {
        int rowCount = Validate(arena, columns, fields);

        int[]? sizes = ArrayPool<int>.Shared.Rent(Math.Max(rowCount, 1));
        int[]? offsets = ArrayPool<int>.Shared.Rent(Math.Max(rowCount, 1));
        byte[]? elements = null;
        try
        {
            int total = ComputeSizes(arena, columns, fields, sizes.AsSpan(0, rowCount));
            _ = ComputeOffsets(sizes.AsSpan(0, rowCount), offsets.AsSpan(0, rowCount));
            elements = ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));

            // The cursors start at zero and end as the per-row sizes, which is why `sizes` can be
            // both the output of pass 1 and the write cursor of pass 2 - the reference does the
            // same, and it is the reason there are three buffers here and not five.
            Span<int> cursors = sizes.AsSpan(0, rowCount);
            cursors.Clear();
            Encode(arena, columns, fields, offsets.AsSpan(0, rowCount), cursors, elements.AsSpan(0, total));

            RowKeys keys = new RowKeys(elements, offsets, sizes, rowCount, total);
            elements = null;
            offsets = null;
            sizes = null;
            return keys;
        }
        finally
        {
            if (elements is not null)
            {
                ArrayPool<byte>.Shared.Return(elements);
            }

            if (offsets is not null)
            {
                ArrayPool<int>.Shared.Return(offsets);
            }

            if (sizes is not null)
            {
                ArrayPool<int>.Shared.Return(sizes);
            }
        }
    }

    /// <summary>Pass 1: how many bytes each row needs.</summary>
    /// <param name="arena">The arena holding the columns.</param>
    /// <param name="columns">One canonical node index per column, in key order.</param>
    /// <param name="fields">One entry per column, in the same order.</param>
    /// <param name="sizes">Receives one size per row; its length is the row count.</param>
    /// <returns>The total number of bytes the encoding needs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="ArgumentException">There are no columns, or the counts disagree.</exception>
    /// <exception cref="VortexUnsupportedException">A column's dtype has no defined ordering.</exception>
    /// <exception cref="VortexFormatException">The output exceeds 2 GiB.</exception>
    public static int ComputeSizes(
        CanonicalArena arena, ReadOnlySpan<int> columns, ReadOnlySpan<RowSortField> fields, Span<int> sizes)
    {
        int rowCount = Validate(arena, columns, fields);
        if (sizes.Length != rowCount)
        {
            throw new ArgumentException(
                $"The columns hold {rowCount} rows but {sizes.Length} sizes were provided.", nameof(sizes));
        }

        sizes.Clear();
        long fixedPerRow = 0;
        for (int c = 0; c < columns.Length; c++)
        {
            RowWidth width = RowWidths.For(arena.GetNode(columns[c]).DType);
            if (width.IsFixed)
            {
                // Not walked at all: a fixed column's contribution is the same integer for every
                // row, so it is added once below rather than rowCount times here.
                fixedPerRow += width.Width;
            }
            else
            {
                RowSizeKernel.Add(arena, columns[c], fields[c], sizes);
            }
        }

        if (fixedPerRow > int.MaxValue)
        {
            throw new VortexFormatException("A row's encoded size exceeds 2 GiB.");
        }

        long total = 0;
        int constant = (int)fixedPerRow;
        for (int i = 0; i < sizes.Length; i++)
        {
            long size = (long)sizes[i] + constant;
            if (size > int.MaxValue)
            {
                throw new VortexFormatException("A row's encoded size exceeds 2 GiB.");
            }

            sizes[i] = (int)size;
            total += size;
            if (total > int.MaxValue)
            {
                throw new VortexFormatException("The row-encoded output exceeds 2 GiB.");
            }
        }

        return (int)total;
    }

    /// <summary>Turns per-row sizes into per-row start offsets.</summary>
    /// <param name="sizes">Per-row sizes from <see cref="ComputeSizes"/>.</param>
    /// <param name="offsets">Receives the exclusive prefix sum; same length as the sizes.</param>
    /// <returns>The total, which is the exclusive prefix sum's final value.</returns>
    /// <exception cref="ArgumentException">The two spans differ in length.</exception>
    /// <exception cref="VortexFormatException">The total exceeds 2 GiB.</exception>
    public static int ComputeOffsets(ReadOnlySpan<int> sizes, Span<int> offsets)
    {
        if (sizes.Length != offsets.Length)
        {
            throw new ArgumentException(
                $"{sizes.Length} sizes cannot fill {offsets.Length} offsets.", nameof(offsets));
        }

        return RowEncodeKernel.Prefix(sizes, offsets);
    }

    /// <summary>Pass 2: write the bytes.</summary>
    /// <param name="arena">The arena holding the columns.</param>
    /// <param name="columns">One canonical node index per column, in key order.</param>
    /// <param name="fields">One entry per column, in the same order.</param>
    /// <param name="offsets">Where each row starts, from <see cref="ComputeOffsets"/>.</param>
    /// <param name="cursors">Zeroed on entry; holds each row's byte count on return.</param>
    /// <param name="destination">Exactly the total <see cref="ComputeSizes"/> returned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="ArgumentException">There are no columns, or the counts disagree.</exception>
    /// <exception cref="VortexUnsupportedException">A column's dtype has no defined ordering.</exception>
    /// <exception cref="VortexFormatException">A column contradicts its dtype.</exception>
    public static void Encode(
        CanonicalArena arena,
        ReadOnlySpan<int> columns,
        ReadOnlySpan<RowSortField> fields,
        ReadOnlySpan<int> offsets,
        Span<int> cursors,
        Span<byte> destination)
    {
        int rowCount = Validate(arena, columns, fields);
        if (offsets.Length != rowCount || cursors.Length != rowCount)
        {
            throw new ArgumentException(
                $"The columns hold {rowCount} rows but {offsets.Length} offsets and " +
                $"{cursors.Length} cursors were provided.", nameof(offsets));
        }

        // Columns are written left to right, so a row's within-key layout is column order: that,
        // and nothing else, is what makes the leading column the primary sort key.
        for (int c = 0; c < columns.Length; c++)
        {
            RowEncodeKernel.Encode(arena, columns[c], fields[c], offsets, cursors, destination);
        }
    }

    /// <summary>
    /// Rejects the shapes both passes would otherwise discover halfway through, and returns the
    /// agreed row count.
    /// </summary>
    private static int Validate(
        CanonicalArena arena, ReadOnlySpan<int> columns, ReadOnlySpan<RowSortField> fields)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (columns.IsEmpty)
        {
            throw new ArgumentException("At least one column is required.", nameof(columns));
        }

        if (fields.Length != columns.Length)
        {
            throw new ArgumentException(
                $"{fields.Length} sort fields were given for {columns.Length} columns.", nameof(fields));
        }

        int rowCount = arena.GetNode(columns[0]).Length;
        for (int c = 0; c < columns.Length; c++)
        {
            CanonicalNode node = arena.GetNode(columns[c]);
            if (node.Length != rowCount)
            {
                throw new ArgumentException(
                    $"Column {c} holds {node.Length} rows where column 0 holds {rowCount}.",
                    nameof(columns));
            }

            // Walks the whole dtype, which is where Extension and List are refused - before any
            // buffer is touched, so an unsupported schema costs nothing.
            _ = RowWidths.For(node.DType);
        }

        return rowCount;
    }
}
