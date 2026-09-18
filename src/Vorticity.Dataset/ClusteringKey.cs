// The clustering key - docs/13-dataset.md §4.1: a level's tree is "ordered by the clustering key
// when the dataset declares one and by first row position otherwise", and §6.1: every data object
// carries "one run per entry", the mandatory run that makes a lookup inside an object a seek.
//
// TWO THINGS FOLLOW FROM DECLARING ONE, and this file is both of them. On the WRITE side, an
// append's policy gains a sorted-runs index on the key columns -- `For` for one column, `ForKey`
// plus a row encoder for several -- whatever policy the caller handed in otherwise. On the TREE
// side, a leaf's key becomes the row encoding of the object's smallest key, which is what puts the
// objects in key order; the encoding is the one of 06, whose `memcmp` order is the tuple order, so
// the tree compares bytes and knows nothing of columns.
//
// AND A SUFFIX, WHICH IS NOT DECORATION. At level 0 the objects OVERLAP -- two appends may both
// start at the same key -- while a tree's keys must be unique. So the leaf key is the encoded
// minimum followed by the object's first row position, eight big-endian bytes. The row encoding is
// self-delimiting, so two different minima differ inside their encodings and the suffix never
// decides; two equal minima fall through to the suffix, which is unique and deterministic. The
// order the tree walks is therefore the key order, with insertion order as the tie-break.
//
// WHAT THE MINIMUM COSTS: one seek on the object's own key cursor, which the mandatory run serves.
// An object with no such cursor -- an imported file this dataset did not write, with no run and no
// sorted key column -- falls back to the per-column minima its summaries carry. For one column that
// IS the minimum. For several it is a tuple at or below the true minimum whose FIRST component is
// right, so objects still order correctly by the leading column and may tie wrongly below it: an
// order that is approximate, never an answer that is wrong, since every scan still reads every
// object the summaries do not refute.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>The columns a dataset is ordered by, and what they impose on a data object.</summary>
public sealed class ClusteringKey
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

    /// <summary>The key a dataset declares, or null when it declares none.</summary>
    /// <param name="paths">The columns, in key order.</param>
    /// <param name="schema">The dataset's schema, which types them.</param>
    /// <returns>The key, or null for an empty list.</returns>
    /// <exception cref="ArgumentException">A path names no column of the schema.</exception>
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

    /// <summary>The write options an object of this dataset is written under (§6.1).</summary>
    /// <param name="options">What the caller asked for.</param>
    /// <returns>The same, with the mandatory run on the key added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public VortexWriteOptions Applied(VortexWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // REQUIRED, because §6.1 says mandatory: on a narrow table a run over one column is
        // comparable in size to that column, so the index budget would abandon it at every object
        // size, and a dataset whose objects have no run cannot be walked in key order at all.
        IndexPolicy run = IndexPolicy.SortedRuns.AsRequired();
        if (!IsComposite)
        {
            return options.WithIndexes(options.Indexes.For(_paths[0], run));
        }

        VortexWriteOptions composite = options.WithIndexes(options.Indexes.ForKey(_paths, run));
        return options.KeyEncoder is null
            ? composite.WithKeyEncoder(new RowKeyEncoder(_fields))
            : composite;
    }

    /// <summary>The row encoding of one tuple, for a seek or a comparison.</summary>
    /// <param name="values">The leading key values, in key order.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentException">More values than the key has columns.</exception>
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

    /// <summary>Opens the object's cursor over this key, or null when it has no source for one.</summary>
    /// <param name="file">The object, open.</param>
    /// <param name="cancellationToken">Cancels the reads the open makes.</param>
    /// <returns>The cursor, which the caller disposes, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <remarks>
    /// Null rather than an exception, because a dataset holds objects it did not write (§3's
    /// import) and one of them having no run is a fact about that object, not a caller error. Every
    /// consumer here has an answer for it.
    /// </remarks>
    public async ValueTask<KeyCursor?> TryOpenAsync(VortexFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            return await file.Keys(_paths).OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VortexUnsupportedException)
        {
            return null;
        }
    }

    /// <summary>The object's smallest key, row-encoded; empty when it has no rows.</summary>
    /// <param name="file">The object, open.</param>
    /// <param name="summaries">Its summaries, for the fallback when it has no key cursor.</param>
    /// <param name="cancellationToken">Cancels the seek.</param>
    /// <returns>The encoded minimum, which is what orders the object's leaf (§4.1).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
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

                // A composite cursor's keys ARE the row encoding; a single column's are values.
                return IsComposite ? cursor.KeyBytes.ToArray() : Encode([cursor.Key]);
            }
        }

        return FromSummaries(summaries);
    }

    /// <summary>The fallback of the file comment: the per-column minima, encoded as a tuple.</summary>
    private byte[] FromSummaries(ObjectSummaries summaries)
    {
        FilterLiteral[] values = new FilterLiteral[_paths.Length];
        for (int i = 0; i < _paths.Length; i++)
        {
            if (!summaries.TryGet(_paths[i], out ColumnSummary column) || !column.HasMin)
            {
                // Nothing is known from here on: a shorter tuple is a prefix of every key that
                // starts with it, which is the order this can still promise.
                return i == 0 ? [] : Encode(values.AsSpan(0, i));
            }

            values[i] = column.Min;
        }

        return Encode(values);
    }

    /// <summary>The dtype of a <c>.</c>-separated path against a struct schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="path">The column.</param>
    /// <returns>Its dtype.</returns>
    /// <exception cref="ArgumentException">The path names no column of the schema.</exception>
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
