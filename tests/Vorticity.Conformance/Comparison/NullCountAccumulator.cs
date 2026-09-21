// `null_counts`, recomputed from the decoded batches.
//
// A row counts as null at a path when the value there is null OR when an ancestor struct is null,
// the number a reader gets by materializing that leaf column. That "or an
// ancestor" clause is the whole reason this exists as a separate check: a struct whose row is null
// while its children's slots hold values is the shape that a per-column null count gets wrong, and
// it is exactly the shape upstream produces.
//
// Paths are `.`-joined field names from the root, the root itself being "". A field name may
// itself contain a '.' or be empty, so the paths are ambiguous by construction for
// such a file; when two different fields produce the same path this accumulator says so instead of
// silently summing them into a number that happens to match.
using System;
using System.Collections.Generic;
using Vorticity.Columns;
using Vorticity.Types;

namespace Vorticity.Conformance.Comparison;

/// <summary>Accumulates per-path null counts across the batches of one scan.</summary>
internal sealed class NullCountAccumulator
{
    private readonly Dictionary<string, long> _counts = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly HashSet<string> _seenThisBatch = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The paths two distinct fields collapsed onto. A struct field may itself be named with a
    /// '.' or be empty, so for such a file these paths are ambiguous by construction and only the
    /// dtype tree, read by index, tells the fields apart. Those paths are named and skipped
    /// rather than compared against a number that two different columns contributed to.
    /// </summary>
    internal HashSet<string> AmbiguousPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The accumulated counts, keyed by path.</summary>
    internal IReadOnlyDictionary<string, long> Counts => _counts;

    /// <summary>Walks one batch's column tree and adds its nulls.</summary>
    /// <param name="batch">The batch. Its spans are read, nothing is retained.</param>
    internal void Accumulate(RecordBatch batch)
    {
        int rows = batch.RowCount;
        if (rows == 0)
        {
            return;
        }

        _seenThisBatch.Clear();
        bool[] ancestorNull = new bool[rows];
        Walk(batch.Root, string.Empty, ancestorNull, rows);
    }

    private void Walk(VortexColumn column, string path, bool[] ancestorNull, int rows)
    {
        bool[] nullHere = new bool[rows];
        long nulls = 0;
        for (int i = 0; i < rows; i++)
        {
            bool isNull = ancestorNull[i] || !column.IsValid(i);
            nullHere[i] = isNull;
            if (isNull)
            {
                nulls++;
            }
        }

        if (!_seenThisBatch.Add(path))
        {
            AmbiguousPaths.Add(path);
        }

        _counts[path] = _counts.TryGetValue(path, out long existing) ? existing + nulls : nulls;

        // Only structs extend the path: the sidecar's `by_path` stops at a list or an extension,
        // which are leaves as far as materializing a column goes.
        if (column.DType.Kind != DTypeKind.Struct)
        {
            return;
        }

        StructColumn fields = column.AsStruct();
        for (int i = 0; i < fields.FieldCount; i++)
        {
            string name = fields.GetFieldName(i);
            string child = path.Length == 0 ? name : path + "." + name;
            Walk(fields.GetField(i), child, nullHere, rows);
        }
    }
}
