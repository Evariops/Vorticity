using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>The columns a dataset is ordered by, and what they impose on a data object.</summary>
internal sealed class ClusteringKey
{
    private readonly string[] _paths;
    private readonly DType[] _dtypes;
    private readonly RowSortField[] _fields;

    private ClusteringKey(string[] paths, DType[] dtypes)
    {
        _paths = paths;
        _dtypes = dtypes;
        _fields = new RowSortField[paths.Length];
        Array.Fill(_fields, RowSortField.Ascending);
    }

    /// <summary>The key a dataset declares, in key order, or null when it declares none.</summary>
    public static ClusteringKey? For(IReadOnlyList<string>? paths, DType schema)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        string[] kept = [.. paths];
        DType[] dtypes = new DType[kept.Length];
        for (int i = 0; i < kept.Length; i++)
        {
            dtypes[i] = Resolve(schema, kept[i]);
        }

        return new ClusteringKey(kept, dtypes);
    }

    /// <summary>The key's columns, in key order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Whether the key is a tuple rather than one column.</summary>
    public bool IsComposite => _paths.Length > 1;

    /// <summary>
    /// Whether an object whose rows come in key order needs no run to be walked by key: one
    /// top-level column of integers that holds no null. Its file statistics then say the column is
    /// sorted, and a sorted column is the source a key cursor and a key-ordered read take before any
    /// run, with the zone map as its index, so a run beside it is bytes nobody reads.
    /// </summary>
    public bool OrdersBySortedColumn =>
        !IsComposite
        && !_paths[0].Contains('.', StringComparison.Ordinal)
        && _dtypes[0].Kind == DTypeKind.Primitive
        && _dtypes[0].PType.IsInteger()
        && !_dtypes[0].IsNullable;

    /// <summary>The caller's write options, with the mandatory sorted run on the key added.</summary>
    public VortexWriteOptions Applied(VortexWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Required rather than budgeted, and spared by the budget: on a narrow table the run is as
        // large as the column it orders, so the budget would refuse it on every object past a
        // mebibyte, compaction outputs included, and a dataset whose objects have no run cannot be
        // walked in key order at all.
        IndexSpec run = IndexSpec.SortedRuns.AsRequired();
        options = options with { BudgetSparesRequired = true };
        if (!IsComposite)
        {
            return options.WithIndexes(options.WritePolicy.For(_paths[0], run));
        }

        VortexWriteOptions composite = options.WithIndexes(options.WritePolicy.ForKey(_paths, run));
        return options.KeyEncoder is null
            ? composite.WithKeyEncoder(new RowKeyEncoder(_fields))
            : composite;
    }

    /// <summary>The row encoding of a leading tuple of key values, for a seek or a comparison.</summary>
    public byte[] Encode(ReadOnlySpan<FilterLiteral> values)
    {
        if (values.Length == 0 || values.Length > _paths.Length)
        {
            throw new ArgumentException(
                $"A key of {_paths.Length} column(s) is encoded from 1 to {_paths.Length} values, not {values.Length}.",
                nameof(values));
        }

        return RowEncoder.EncodeKey(
            values, _dtypes.AsSpan(0, values.Length), _fields.AsSpan(0, values.Length));
    }

    /// <summary>
    /// The row encoding of the key <paramref name="cursor"/> is on, a single column's, written into
    /// <paramref name="scratch"/>, which grows when it is too small: the bytes <see cref="Encode"/>
    /// gives for the key, without an array for every key of a walk. A composite cursor's keys are
    /// the encoding already, and need none of this.
    /// </summary>
    /// <returns>How many bytes of <paramref name="scratch"/> hold the encoding.</returns>
    public int EncodeCurrent(KeyCursor cursor, ref byte[] scratch)
    {
        DType dtype = _dtypes[0];
        RowSortField field = _fields[0];
        if (cursor.KeyKind == FilterLiteralKind.Bytes)
        {
            // Lent by the cursor, where its key would be copied into a literal.
            ReadOnlySpan<byte> bytes = cursor.KeyBytes;
            Reserve(ref scratch, RowEncoder.BytesLength(bytes.Length));
            return RowEncoder.WriteBytes(bytes, field, scratch);
        }

        FilterLiteral key = cursor.Key;
        Reserve(ref scratch, RowEncoder.ValueLength(key, dtype));
        return RowEncoder.WriteValue(key, dtype, field, scratch);
    }

    private static void Reserve(ref byte[] scratch, int length)
    {
        if (scratch.Length < length)
        {
            scratch = new byte[Math.Max(length, 2 * scratch.Length)];
        }
    }

    /// <summary>
    /// Opens the object's cursor over this key, which the caller disposes, or null when the object
    /// carries no run on the key. Null rather than an exception: a dataset may hold imported objects
    /// it did not write, and every consumer here has an answer for one.
    /// </summary>
    public ValueTask<KeyCursor?> TryOpenAsync(VortexFile file, CancellationToken cancellationToken) =>
        TryOpenAsync(file, indexes: true, cancellationToken);

    /// <summary>
    /// <see cref="TryOpenAsync(VortexFile, CancellationToken)"/>, from the file's key indexes or,
    /// without <paramref name="indexes"/>, from a column its statistics say is sorted only.
    /// </summary>
    public ValueTask<KeyCursor?> TryOpenAsync(VortexFile file, bool indexes, CancellationToken cancellationToken) =>
        TryOpenAsync(file, _paths, indexes, cancellationToken);

    /// <summary>
    /// <see cref="TryOpenAsync(VortexFile, bool, CancellationToken)"/> over the key's columns as the
    /// object names them: an object written under an earlier schema may hold a column under the name
    /// it had then.
    /// </summary>
    public ValueTask<KeyCursor?> TryOpenAsync(VortexFile file, IReadOnlyList<string> paths, bool indexes, CancellationToken cancellationToken) =>
        TryOpenAsync(file, paths, indexes, DeletionVector.Empty, ranksAreRows: false, cancellationToken);

    /// <summary>
    /// <see cref="TryOpenAsync(VortexFile, IReadOnlyList{string}, bool, CancellationToken)"/> without
    /// the entries of the rows <paramref name="deletions"/> names: no step lands on one and no rank
    /// counts one. With <paramref name="ranksAreRows"/> the file's rows are in this key's order, null
    /// keys last, and the ranks follow from the rows alone; otherwise the deleted rows' keys are read
    /// when the source needs them.
    /// </summary>
    public async ValueTask<KeyCursor?> TryOpenAsync(
        VortexFile file, IReadOnlyList<string> paths, bool indexes, DeletionVector deletions, bool ranksAreRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            KeyCursorBuilder keys = file.Keys(paths as string[] ?? [.. paths]);
            if (!deletions.IsEmpty)
            {
                keys.Excluding(deletions, ranksAreRows, token => LiveRows.ExcludedKeysAsync(file, deletions, paths, this, token));
            }

            return await (indexes ? keys : keys.WithSource(KeySourceKind.SortedColumn))
                .OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VortexUnsupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The object's smallest key, row-encoded, which is what orders its leaf; empty when it has no
    /// rows. Without a key cursor this falls back to the summaries and may be below the true
    /// minimum, so the order is approximate while no scan result changes.
    /// </summary>
    public async ValueTask<byte[]> MinimumAsync(
        VortexFile file, ObjectSummaries summaries, CancellationToken cancellationToken)
    {
        KeyCursor? cursor = await TryOpenAsync(file, cancellationToken).ConfigureAwait(false);
        if (cursor is not null)
        {
            await using (cursor.ConfigureAwait(false))
            {
                if (!await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false))
                {
                    return [];
                }

                // A composite cursor's keys are already the row encoding; a single column's are values.
                return IsComposite ? cursor.KeyBytes.ToArray() : Encode([cursor.Key]);
            }
        }

        return FromSummaries(summaries);
    }

    /// <summary>The per-column minima, encoded as a tuple.</summary>
    private byte[] FromSummaries(ObjectSummaries summaries)
    {
        FilterLiteral[] values = new FilterLiteral[_paths.Length];
        for (int i = 0; i < _paths.Length; i++)
        {
            if (!summaries.TryGet(_paths[i], out ColumnSummary column) || !column.HasMin)
            {
                // Nothing is known from here on; a shorter tuple is a prefix of every key that
                // starts with it, which is the order this can still promise.
                return i == 0 ? [] : Encode(values.AsSpan(0, i));
            }

            values[i] = column.Min;
        }

        return Encode(values);
    }

    /// <summary>The dtype of a <c>.</c>-separated path against a struct schema.</summary>
    internal static DType Resolve(DType schema, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DType at = schema;
        foreach (string segment in path.Split('.'))
        {
            if (at.Kind != DTypeKind.Struct)
            {
                throw new ArgumentException(
                    $"'{path}' descends into a {at.Kind}, which has no fields.", nameof(path));
            }

            int index = at.IndexOfField(segment);
            if (index < 0)
            {
                throw new ArgumentException($"'{path}' names no column of the schema.", nameof(path));
            }

            at = at.GetField(index);
        }

        return at;
    }
}
