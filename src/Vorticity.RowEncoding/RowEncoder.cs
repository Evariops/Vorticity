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
/// The bytes carry no type tags, no field names and no sort options, so two keys are comparable
/// only when they came from the same schema with the same options, and the layout may change
/// between Vortex releases. Supported are nulls, booleans, every integer and float, decimals up to
/// 128 bits, text, binary, nested records and fixed-size lists, nested arbitrarily; a date, time,
/// timestamp or other extension column is ordered by its storage. Variable-size lists, maps,
/// variants, unions and 256-bit decimals raise <see cref="VortexUnsupportedException"/> because the
/// format defines no order for them. NaNs are not canonicalized, so two NaNs with different
/// payloads differ.
/// </remarks>
public static partial class RowEncoder
{
    /// <summary>
    /// The Vortex release whose byte layout this encoder reproduces; with the package version, it
    /// is what tells a consumer whether two sets of keys are comparable.
    /// </summary>
    public const string VortexVersion = "0.86.1";

    /// <summary>Encodes the members of a record, one column per member in declaration order.</summary>
    /// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
    /// <param name="columns">The batch's columns.</param>
    /// <param name="fields">One sort field per member, one for every member, or none for ascending with nulls first.</param>
    /// <returns>One key per row of the batch, selected or not; the caller disposes it.</returns>
    /// <exception cref="VortexUnsupportedException">A member's column has no order the format defines.</exception>
    public static RowKeys Encode<TRecord>(Columns<TRecord> columns, params ReadOnlySpan<RowSortField> fields)
        where TRecord : IVortexRecord<TRecord>
    {
        int count = columns.MemberCount;
        Span<int> nodes = count <= 32 ? stackalloc int[count] : new int[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = Storage(columns.Arena, columns.ColumnNode(i));
        }

        return Encode(columns.Arena, nodes, PerColumn(fields, count));
    }

    /// <summary>Encodes the columns of a batch, in schema order.</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="fields">One sort field per column, one for every column, or none for ascending with nulls first.</param>
    /// <returns>One key per row of the batch, selected or not; the caller disposes it.</returns>
    /// <exception cref="VortexUnsupportedException">A column has no order the format defines.</exception>
    public static RowKeys Encode(BatchView batch, params ReadOnlySpan<RowSortField> fields)
    {
        int count = batch.Schema.Count;
        Span<int> nodes = count <= 32 ? stackalloc int[count] : new int[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = Storage(batch.Arena, batch.ColumnNode(i));
        }

        return Encode(batch.Arena, nodes, PerColumn(fields, count));
    }

    /// <summary>Encodes the fields of a batch's root struct, one column per field, taking one entry of <paramref name="fields"/> per root field in schema order.</summary>
    internal static RowKeys Encode(RecordBatch batch, ReadOnlySpan<RowSortField> fields)
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

    /// <summary>The storage below every extension wrapping <paramref name="node"/>.</summary>
    internal static int Storage(CanonicalArena arena, int node)
    {
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        return node;
    }

    /// <summary>One sort field per column: <paramref name="fields"/> itself, or its single entry or the default repeated.</summary>
    private static ReadOnlySpan<RowSortField> PerColumn(ReadOnlySpan<RowSortField> fields, int columns)
    {
        if (fields.Length == columns || fields.Length > 1)
        {
            return fields;
        }

        RowSortField[] each = new RowSortField[columns];
        each.AsSpan().Fill(fields.IsEmpty ? RowSortField.Ascending : fields[0]);
        return each;
    }

    /// <summary>
    /// Encodes columns, given one canonical node index and one sort field per column in key order,
    /// into pooled row keys the caller disposes.
    /// </summary>
    internal static RowKeys Encode(
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

            // Cursors start at zero and end as the per-row sizes, so one buffer serves as both the
            // output of pass 1 and the write cursor of pass 2.
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

    /// <summary>
    /// Pass 1: fills <paramref name="sizes"/>, one entry per row, and returns the total number of
    /// bytes the encoding needs.
    /// </summary>
    internal static int ComputeSizes(
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
                // A fixed column contributes the same integer to every row, so it is added once
                // below rather than walked.
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

    /// <summary>
    /// Turns per-row sizes into per-row start offsets, the exclusive prefix sum, and returns its
    /// final value.
    /// </summary>
    internal static int ComputeOffsets(ReadOnlySpan<int> sizes, Span<int> offsets)
    {
        if (sizes.Length != offsets.Length)
        {
            throw new ArgumentException(
                $"{sizes.Length} sizes cannot fill {offsets.Length} offsets.", nameof(offsets));
        }

        return RowEncodeKernel.Prefix(sizes, offsets);
    }

    /// <summary>
    /// Pass 2: writes the bytes into a <paramref name="destination"/> of exactly the total
    /// <see cref="ComputeSizes"/> returned. <paramref name="cursors"/> is zeroed on entry and
    /// holds each row's byte count on return.
    /// </summary>
    internal static void Encode(
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

        // Columns are written left to right, which is what makes the leading column the primary
        // sort key.
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

            // Walks the whole dtype so an unsupported one is refused before any buffer is touched.
            _ = RowWidths.For(node.DType);
        }

        return rowCount;
    }
}
