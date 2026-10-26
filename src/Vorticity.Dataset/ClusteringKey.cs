using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>The columns a dataset is ordered by, and what they impose on a data object.</summary>
internal sealed class ClusteringKey
{
    private readonly string[] _paths;
    private readonly DType[] _dtypes;
    private readonly RowSortField[] _fields;

    private ClusteringKey(string[] paths, DType[] dtypes, bool mayHoldNull)
    {
        _paths = paths;
        _dtypes = dtypes;
        MayHoldNull = mayHoldNull;
        _fields = new RowSortField[paths.Length];
        Array.Fill(_fields, RowSortField.Ascending);
    }

    /// <summary>The key a dataset declares, in key order, or null when it declares none.</summary>
    /// <param name="paths">The key's columns, in key order.</param>
    /// <param name="schema">The dataset's schema.</param>
    public static ClusteringKey? For(IReadOnlyList<string>? paths, DType schema)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        string[] kept = [.. paths];
        DType[] dtypes = new DType[kept.Length];
        bool mayHoldNull = false;
        for (int i = 0; i < kept.Length; i++)
        {
            dtypes[i] = Resolve(schema, kept[i]);
            mayHoldNull |= ColumnPath.MayBeNull(schema, kept[i]);
        }

        return new ClusteringKey(kept, dtypes, mayHoldNull);
    }

    /// <summary>The key's first column: the one a clustering value is a function of, and the one a level's ranges are judged on.</summary>
    public string InstantPath => _paths[0];

    /// <summary>
    /// The key a new dataset declares, which may not be over a column that may hold a null. A
    /// composite key's run holds no tuple with a null, so no merge could keep such a row. And a null
    /// has no place in the order a level above 0 is disjoint on: every null is the same key, which no
    /// merge cuts between two objects, so the nulls of every merge would stay in an object of their
    /// own merge, each read only after every other key.
    /// </summary>
    /// <remarks>A dataset created with a key of one such column before it was refused still opens, reads and writes.</remarks>
    /// <param name="paths">The key's columns, in key order.</param>
    /// <param name="schema">The dataset's schema.</param>
    /// <param name="acceptsNullable">Whether a key of one column that may hold a null is accepted, as it was before: the tests of such datasets.</param>
    /// <exception cref="ArgumentException">A path names no column, or a column of the key, or a struct on its path, may hold a null.</exception>
    public static ClusteringKey? Declared(IReadOnlyList<string>? paths, DType schema, bool acceptsNullable = false)
    {
        ClusteringKey? key = For(paths, schema);
        if (key is { MayHoldNull: true } && (key.IsComposite || !acceptsNullable))
        {
            throw new ArgumentException(
                key.IsComposite
                    ? $"The clustering key ({string.Join(", ", key.Paths)}) is a tuple over a column that may hold a null, and a " +
                      "tuple's run holds none: its rows could never be compacted. Declare a composite key on non-nullable columns."
                    : $"The clustering key '{key.Paths[0]}' may hold a null, which has no place in the order the levels of a " +
                      "dataset are disjoint on. Declare it on a non-nullable column, and give a row whose key is unknown a value " +
                      "of its own, the smallest or the largest the type has.",
                nameof(paths));
        }

        return key;
    }

    /// <summary>Whether a column of the key, or a struct on its path, is nullable.</summary>
    public bool MayHoldNull { get; }

    /// <summary>
    /// Whether an object's entry states its last key (<see cref="ObjectEntry.LastKey"/>): a composite key, whose leading
    /// column's bounds take every object sharing a value of it, or a column a file's statistics do not bound, a text's.
    /// A number's or a time's statistics bound a key of that column alone exactly, and its entries keep the commit
    /// format the dataset would write without the field.
    /// </summary>
    public bool StatesLastKey
    {
        get
        {
            DType leading = _dtypes[0];
            while (leading.Kind == DTypeKind.Extension)
            {
                leading = leading.StorageType;
            }

            return IsComposite || leading.Kind != DTypeKind.Primitive;
        }
    }

    /// <summary>The dtype of the key's first column.</summary>
    internal DType LeadingType => _dtypes[0];

    /// <summary>The dtype of the key's column at <paramref name="position"/>.</summary>
    internal DType TypeAt(int position) => _dtypes[position];

    /// <summary>
    /// <paramref name="summaries"/> with the key's first column bounded by an object's smallest key <paramref name="first"/>
    /// and its largest <paramref name="last"/>, where they do not bound it: a text's, which a file's statistics leave
    /// unbounded. The first value of each, row-encoded, read back, and no null, which the key's order would put at one end:
    /// what a filter or a key on the column is then proven by over the object, as a number's statistics prove it. The
    /// summaries as they are when they bound the column, or when a key's first value is a null.
    /// </summary>
    /// <param name="summaries">The object's summaries, as its entry holds them.</param>
    /// <param name="first">Its smallest key, row-encoded.</param>
    /// <param name="last">Its largest key, row-encoded.</param>
    /// <param name="exact">Whether <paramref name="last"/> is the object's largest key, not a bound a delete left above it.</param>
    internal ObjectSummaries Bounded(ObjectSummaries summaries, ReadOnlySpan<byte> first, ReadOnlySpan<byte> last, bool exact)
    {
        if (summaries.TryGet(_paths[0], out ColumnSummary held) && held.HasMin && held.HasMax)
        {
            return summaries;
        }

        FilterLiteral low = RowEncoder.ReadFirstValue(first, _dtypes[0]);
        FilterLiteral high = RowEncoder.ReadFirstValue(last, _dtypes[0]);
        if (low.Kind == FilterLiteralKind.Null || high.Kind == FilterLiteralKind.Null)
        {
            return summaries;
        }

        List<ColumnSummary> columns = [new ColumnSummary(_paths[0], low, true, high, true, exact, 0, true)];
        foreach (ColumnSummary column in summaries.Columns)
        {
            if (!string.Equals(column.Path, _paths[0], StringComparison.Ordinal))
            {
                columns.Add(column);
            }
        }

        return ObjectSummaries.From(columns);
    }

    /// <summary>
    /// <paramref name="summaries"/> with the key's first column bounded by <paramref name="low"/> and <paramref name="high"/>,
    /// those of a part of the rows they summarize, a rollup's bucket of one clustering value, a function of that column:
    /// what a filter or a key on the column is then proven by over the part. The other columns keep the whole's bounds,
    /// which hold over any part of it.
    /// </summary>
    internal ObjectSummaries Within(ObjectSummaries summaries, FilterLiteral low, FilterLiteral high)
    {
        bool held = summaries.TryGet(_paths[0], out ColumnSummary whole);
        List<ColumnSummary> columns = [new ColumnSummary(_paths[0], low, true, high, true, held && whole.IsExact, held ? whole.NullCount : 0, held && whole.HasNullCount)];
        foreach (ColumnSummary column in summaries.Columns)
        {
            if (!string.Equals(column.Path, _paths[0], StringComparison.Ordinal))
            {
                columns.Add(column);
            }
        }

        return ObjectSummaries.From(columns);
    }

    /// <summary>The key's columns, in key order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Whether the key is a tuple rather than one column.</summary>
    public bool IsComposite => _paths.Length > 1;

    /// <summary>
    /// Whether an object whose rows come in key order needs no run to be walked by key: one
    /// top-level column of integers that holds no null, or of an extension stored as integers, an
    /// instant or a date. Its file statistics then say the column is sorted, and a sorted column is
    /// the source a key cursor and a key-ordered read take before any run, with the zone map as its
    /// index, so a run beside it is bytes nobody reads: as much as the column itself.
    /// </summary>
    public bool OrdersBySortedColumn
    {
        get
        {
            DType storage = _dtypes[0];
            while (storage.Kind == DTypeKind.Extension)
            {
                storage = storage.StorageType;
            }

            return !IsComposite
                && !_paths[0].Contains('.', StringComparison.Ordinal)
                && storage.Kind == DTypeKind.Primitive
                && storage.PType.IsInteger()
                && !_dtypes[0].IsNullable;
        }
    }

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
        VortexFile file, ObjectSummaries summaries, CancellationToken cancellationToken) =>
        (await BoundsAsync(file, summaries, cancellationToken).ConfigureAwait(false)).First;

    /// <summary>
    /// The object's smallest and largest keys, row-encoded, from one key cursor: what orders its leaf, and what its entry
    /// bounds its keys by (<see cref="ObjectEntry.LastKey"/>). Without a key cursor the smallest falls back to the
    /// summaries, as <see cref="MinimumAsync"/> says, and the largest is empty, which states nothing; so it is for a key
    /// that may hold a null, which a cursor puts last and the encoding first.
    /// </summary>
    public async ValueTask<(byte[] First, byte[] Last)> BoundsAsync(
        VortexFile file, ObjectSummaries summaries, CancellationToken cancellationToken)
    {
        KeyCursor? cursor = await TryOpenAsync(file, cancellationToken).ConfigureAwait(false);
        if (cursor is not null)
        {
            await using (cursor.ConfigureAwait(false))
            {
                if (!await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ([], []);
                }

                // A composite cursor's keys are already the row encoding; a single column's are values.
                byte[] first = Current(cursor);
                return !MayHoldNull && await cursor.SeekLastAsync(cancellationToken).ConfigureAwait(false) ? (first, Current(cursor)) : (first, []);
            }
        }

        return (FromSummaries(summaries), []);
    }

    /// <summary>The key a cursor is on, row-encoded.</summary>
    private byte[] Current(KeyCursor cursor) => IsComposite ? cursor.KeyBytes.ToArray() : Encode([cursor.Key]);

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
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            if (at.Kind != DTypeKind.Struct)
            {
                throw new ArgumentException(
                    $"'{path}' descends into a {at.Kind}, which has no fields.", nameof(path));
            }

            int index = ColumnPath.IndexOf(at, rest[segment]);
            if (index < 0)
            {
                throw new ArgumentException($"'{path}' names no column of the schema.", nameof(path));
            }

            at = at.GetField(index);
        }

        return at;
    }
}
