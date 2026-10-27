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

    private ClusteringKey(string[] paths, DType[] dtypes, bool mayHoldNull, bool inRowOrder)
    {
        _paths = paths;
        _dtypes = dtypes;
        MayHoldNull = mayHoldNull;
        InRowOrder = inRowOrder;
        _fields = new RowSortField[paths.Length];

        // Ascending, nulls last, as the tree, the merges and the sorted objects order every key.
        Array.Fill(_fields, KeyOrderedMerge.Field(descending: false));
    }

    /// <summary>
    /// The key a dataset's layout declares over columns as they are, in key order, or null when it declares none: every
    /// object's rows are in its order, nulls last (<see cref="InRowOrder"/>).
    /// </summary>
    /// <param name="paths">The key's columns, in key order.</param>
    /// <param name="schema">The dataset's schema.</param>
    public static ClusteringKey? OfLayout(IReadOnlyList<string>? paths, DType schema) => Of(paths, schema, inRowOrder: true);

    /// <summary>A key over <paramref name="paths"/>, in key order, which the objects' rows need not follow; null for none.</summary>
    /// <param name="paths">The key's columns, in key order.</param>
    /// <param name="schema">The dataset's schema.</param>
    public static ClusteringKey? For(IReadOnlyList<string>? paths, DType schema) => Of(paths, schema, inRowOrder: false);

    private static ClusteringKey? Of(IReadOnlyList<string>? paths, DType schema, bool inRowOrder)
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

        return new ClusteringKey(kept, dtypes, mayHoldNull, inRowOrder);
    }

    /// <summary>
    /// Whether every object's rows are in this key's order, nulls last, as the dataset's writers sort them: its key is the
    /// layout's. An object's own rows are then its entries in key order, and no index stands in for them.
    /// </summary>
    public bool InRowOrder { get; }

    /// <summary>The key's first column: the one a clustering value is a function of, and the one a level's ranges are judged on.</summary>
    public string InstantPath => _paths[0];

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
    /// Opens the object's cursor over this key, which the caller disposes, or null when nothing in the object serves the
    /// key. On the layout's key (<see cref="InRowOrder"/>) the object's rows are its entries, read in their order: a single
    /// column's values with the zone map as their index, a tuple's encoding with the sparse index as theirs. On another
    /// key, the file's own sources: a column its statistics call sorted, or a run its policy asked for.
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
        VortexFile file, IReadOnlyList<string> paths, bool indexes, DeletionVector deletions, bool ranksAreRows, CancellationToken cancellationToken,
        GranuleKeys? index = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (InRowOrder && IsComposite)
        {
            KeySource tuples = new SortedColumnWalker(new ObjectKeyEntries(file, [.. paths], index));
            return new KeyCursor(deletions.IsEmpty ? tuples : new ExcludingKeySource(tuples, deletions, 0));
        }

        try
        {
            KeyCursorBuilder keys = InRowOrder ? file.Keys(paths[0]).InRowOrder() : file.Keys(paths as string[] ?? [.. paths]);
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
    /// summaries, as <see cref="MinimumAsync"/> says, and the largest is empty, which states nothing.
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
                return await cursor.SeekLastAsync(cancellationToken).ConfigureAwait(false) ? (first, Current(cursor)) : (first, []);
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
