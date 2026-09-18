// The bounded summaries a node carries - docs/13-dataset.md §4.2: "summaries, bounded: per
// summarised column, `min`, `max`, `null_count` [...] Summarised columns are the first 32 leaf
// columns by default or the declared list, so an entry has a bounded size whatever the schema",
// and, for an internal entry, "the union of its children's key ranges and summaries", because
// "pruning happens at every level: a predicate that the node's summaries refute skips the whole
// subtree".
//
// THE UNION IS INTERSECTION-SHAPED, and getting that backwards is how a dataset loses a row. The
// union's bound over a column is the loosest of its parts: min of the mins, max of the maxes, the
// nulls summed. But a part that says NOTHING about a column says its rows may hold anything, so the
// union must say nothing about it either -- a column is carried up only when every part carries it,
// and a bound only when every part has that bound. Keeping a child's min because the other child
// was silent would prune a subtree that holds the answer.
//
// CANONICAL, because these bytes sit inside a content-addressed page and §4.1 promises "the same
// objects committed in any order give byte-identical pages and one root hash". So: sorted by path,
// no optional field that could be written two ways, the smallest varint that holds each length, and
// a literal written by its comparison kind rather than by its dtype -- the same collapse
// `FilterLiteral` makes, so that two files whose `i32` and `i16` columns share a value summarise to
// the same bytes.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Dataset;

/// <summary>What a node says about its columns, without anything being opened.</summary>
public sealed class ObjectSummaries : IEquatable<ObjectSummaries>
{
    private static readonly ObjectSummaries None = new ObjectSummaries([]);
    private readonly ColumnSummary[] _columns;

    private ObjectSummaries(ColumnSummary[] columns) => _columns = columns;

    /// <summary>Summaries of nothing, which refute nothing.</summary>
    public static ObjectSummaries Empty => None;

    /// <summary>How many columns are summarised.</summary>
    public int Count => _columns.Length;

    /// <summary>The summarised columns, in path order.</summary>
    public IReadOnlyList<ColumnSummary> Columns => _columns;

    /// <summary>The summaries of a file's columns, as §4.2 bounds them.</summary>
    /// <param name="file">An open data object; no data segment is read.</param>
    /// <param name="paths">The declared list, or null for the first <paramref name="limit"/> columns.</param>
    /// <param name="limit">How many columns to summarise when no list is declared.</param>
    /// <returns>The summaries.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static ObjectSummaries Of(
        VortexFile file, IReadOnlyList<string>? paths = null, int limit = ColumnSummary.DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(file);
        IReadOnlyList<ColumnSummary> summaries = paths is null
            ? ColumnSummaries.Of(file, limit)
            : ColumnSummaries.Of(file, paths);
        return From(summaries);
    }

    /// <summary>The summaries of a list of columns, sorted into the canonical order.</summary>
    /// <param name="summaries">The columns, in any order; a duplicate path is a caller error.</param>
    /// <returns>The summaries. A column that says nothing is dropped: it has no encoding to have.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summaries"/> is null.</exception>
    /// <exception cref="ArgumentException">Two summaries name the same column.</exception>
    public static ObjectSummaries From(IReadOnlyList<ColumnSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        List<ColumnSummary> kept = new List<ColumnSummary>(summaries.Count);
        for (int i = 0; i < summaries.Count; i++)
        {
            if (!summaries[i].IsEmpty)
            {
                kept.Add(summaries[i]);
            }
        }

        if (kept.Count == 0)
        {
            return None;
        }

        ColumnSummary[] columns = [.. kept];
        Array.Sort(columns, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        for (int i = 1; i < columns.Length; i++)
        {
            if (string.Equals(columns[i - 1].Path, columns[i].Path, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{columns[i].Path}' is summarised twice; a node carries one summary per column.",
                    nameof(summaries));
            }
        }

        return new ObjectSummaries(columns);
    }

    /// <summary>The union of a page's entries' summaries, as an internal entry carries it (§4.2).</summary>
    /// <param name="parts">The children's summaries.</param>
    /// <returns>The union: the loosest bound over every part, and silence wherever a part is silent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is null.</exception>
    public static ObjectSummaries Union(IReadOnlyList<ObjectSummaries> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return None;
        }

        ObjectSummaries first = parts[0];
        if (first.Count == 0)
        {
            return None;
        }

        List<ColumnSummary> union = new List<ColumnSummary>(first.Count);
        for (int column = 0; column < first.Count; column++)
        {
            ColumnSummary merged = first._columns[column];
            bool everywhere = true;
            for (int part = 1; part < parts.Count && everywhere; part++)
            {
                everywhere = parts[part].TryGet(merged.Path, out ColumnSummary other);
                if (everywhere)
                {
                    merged = Union(merged, other);
                }
            }

            if (everywhere && !merged.IsEmpty)
            {
                union.Add(merged);
            }
        }

        return union.Count == 0 ? None : new ObjectSummaries([.. union]);
    }

    /// <summary>Whether a row the predicate selects can lie under this node.</summary>
    /// <param name="pruner">The predicate, prepared once for the whole walk.</param>
    /// <param name="rows">The rows the node covers.</param>
    /// <returns><see langword="false"/> only when these summaries prove no row can match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pruner"/> is null.</exception>
    public bool MayMatch(SummaryPruner pruner, long rows)
    {
        ArgumentNullException.ThrowIfNull(pruner);
        return pruner.MayMatch(_columns, rows);
    }

    /// <summary>The summary of one column, when it is carried.</summary>
    /// <param name="path">The column.</param>
    /// <param name="summary">Receives it.</param>
    /// <returns>Whether the column is summarised.</returns>
    public bool TryGet(string path, out ColumnSummary summary)
    {
        int low = 0;
        int high = _columns.Length - 1;
        while (low <= high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            int order = string.CompareOrdinal(_columns[middle].Path, path);
            if (order == 0)
            {
                summary = _columns[middle];
                return true;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        summary = default;
        return false;
    }

    /// <summary>These summaries' canonical bytes.</summary>
    /// <returns>The bytes; empty when nothing is summarised.</returns>
    public byte[] ToBytes()
    {
        if (_columns.Length == 0)
        {
            return [];
        }

        byte[][] paths = new byte[_columns.Length][];
        int bytes = TreePage.VarintBytes((ulong)_columns.Length);
        for (int i = 0; i < _columns.Length; i++)
        {
            paths[i] = Encoding.UTF8.GetBytes(_columns[i].Path);
            bytes += TreePage.VarintBytes((ulong)paths[i].Length) + paths[i].Length + 1;
            if (_columns[i].HasMin)
            {
                bytes += LiteralBytes(_columns[i].Min);
            }

            if (_columns[i].HasMax)
            {
                bytes += LiteralBytes(_columns[i].Max);
            }

            if (_columns[i].HasNullCount)
            {
                bytes += TreePage.VarintBytes((ulong)_columns[i].NullCount);
            }
        }

        byte[] value = new byte[bytes];
        Span<byte> at = WriteVarint(value, (ulong)_columns.Length);
        for (int i = 0; i < _columns.Length; i++)
        {
            ColumnSummary column = _columns[i];
            at = WriteVarint(at, (ulong)paths[i].Length);
            paths[i].CopyTo(at);
            at = at[paths[i].Length..];
            at[0] = Flags(column);
            at = at[1..];
            if (column.HasMin)
            {
                at = WriteLiteral(at, column.Min);
            }

            if (column.HasMax)
            {
                at = WriteLiteral(at, column.Max);
            }

            if (column.HasNullCount)
            {
                at = WriteVarint(at, (ulong)column.NullCount);
            }
        }

        return at.IsEmpty ? value : throw new CommitFormatException("A node's summaries were mis-sized.");
    }

    /// <summary>Reads summaries written by <see cref="ToBytes"/>.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The summaries.</returns>
    /// <exception cref="CommitFormatException">The bytes are not a node's summaries.</exception>
    public static ObjectSummaries FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return None;
        }

        int at = 0;
        int count = checked((int)ReadVarint(value, ref at));
        ColumnSummary[] columns = new ColumnSummary[count];
        for (int i = 0; i < count; i++)
        {
            int length = checked((int)ReadVarint(value, ref at));
            if (at + length > value.Length)
            {
                throw new CommitFormatException("A summarised column's path runs past its bytes.");
            }

            string path = Encoding.UTF8.GetString(value.Slice(at, length));
            at += length;
            if (at >= value.Length)
            {
                throw new CommitFormatException("A summarised column ends before its flags.");
            }

            byte flags = value[at++];
            if ((flags & (HasMinFlag | HasMaxFlag | HasNullsFlag)) == 0)
            {
                // A column that says nothing has no encoding: allowing one would be two encodings
                // of one set of bounds, and these bytes sit inside a content-addressed page.
                throw new CommitFormatException($"'{path}' is summarised with no bound and no count.");
            }

            FilterLiteral min = (flags & HasMinFlag) != 0 ? ReadLiteral(value, ref at) : default;
            FilterLiteral max = (flags & HasMaxFlag) != 0 ? ReadLiteral(value, ref at) : default;
            long nulls = (flags & HasNullsFlag) != 0 ? (long)ReadVarint(value, ref at) : 0;
            columns[i] = new ColumnSummary(
                path,
                min,
                (flags & HasMinFlag) != 0,
                max,
                (flags & HasMaxFlag) != 0,
                (flags & ExactFlag) != 0,
                nulls,
                (flags & HasNullsFlag) != 0);
            if (i > 0 && string.CompareOrdinal(columns[i - 1].Path, path) >= 0)
            {
                throw new CommitFormatException(
                    $"'{path}' does not come after '{columns[i - 1].Path}'; summaries are canonical and sorted.");
            }
        }

        if (at != value.Length)
        {
            throw new CommitFormatException($"A node's summaries have {value.Length - at} bytes left over.");
        }

        return count == 0 ? None : new ObjectSummaries(columns);
    }

    /// <summary>Whether two sets of summaries say the same thing about the same columns.</summary>
    /// <param name="other">The other set.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    /// BY VALUE, because an <see cref="ObjectEntry"/> is a record and the protocol of §8.2 compares
    /// entries to decide whether a rebase changed anything. Reference equality here would make a
    /// re-applied operation look like a different one and rewrite a page for nothing.
    /// </remarks>
    public bool Equals(ObjectSummaries? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._columns.Length != _columns.Length)
        {
            return false;
        }

        for (int i = 0; i < _columns.Length; i++)
        {
            if (!_columns[i].Equals(other._columns[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ObjectSummaries);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_columns.Length);
        foreach (ColumnSummary column in _columns)
        {
            hash.Add(column.Path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private const byte HasMinFlag = 1;
    private const byte HasMaxFlag = 2;
    private const byte HasNullsFlag = 4;
    private const byte ExactFlag = 8;

    private static byte Flags(ColumnSummary column) => (byte)(
        (column.HasMin ? HasMinFlag : 0)
        | (column.HasMax ? HasMaxFlag : 0)
        | (column.HasNullCount ? HasNullsFlag : 0)
        | (column.IsExact ? ExactFlag : 0));

    /// <summary>The loosest summary that covers both.</summary>
    private static ColumnSummary Union(ColumnSummary left, ColumnSummary right)
    {
        bool hasMin = left.HasMin && right.HasMin && Comparable(left.Min, right.Min);
        bool hasMax = left.HasMax && right.HasMax && Comparable(left.Max, right.Max);
        bool hasNulls = left.HasNullCount && right.HasNullCount;
        return new ColumnSummary(
            left.Path,
            hasMin ? (Compare(left.Min, right.Min) <= 0 ? left.Min : right.Min) : default,
            hasMin,
            hasMax ? (Compare(left.Max, right.Max) >= 0 ? left.Max : right.Max) : default,
            hasMax,
            left.IsExact && right.IsExact,
            hasNulls ? SaturatingSum(left.NullCount, right.NullCount) : 0,
            hasNulls);
    }

    private static long SaturatingSum(long left, long right) =>
        long.MaxValue - left < right ? long.MaxValue : left + right;

    /// <summary>
    /// Whether two bounds can be ordered at all: the same comparison kind, and one that has an
    /// order. Two columns of one dataset share a dtype, so a mismatch means something upstream is
    /// wrong -- and dropping the bound is the answer that cannot lose a row.
    /// </summary>
    private static bool Comparable(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind != FilterLiteralKind.Null;

    private static int Compare(FilterLiteral left, FilterLiteral right) => left.Kind switch
    {
        FilterLiteralKind.Bool => left.BoolValue.CompareTo(right.BoolValue),
        FilterLiteralKind.Signed => left.SignedValue.CompareTo(right.SignedValue),
        FilterLiteralKind.Unsigned => left.UnsignedValue.CompareTo(right.UnsignedValue),
        FilterLiteralKind.Float => left.FloatValue.CompareTo(right.FloatValue),
        FilterLiteralKind.Bytes => left.BytesValue.SequenceCompareTo(right.BytesValue),
        _ => 0,
    };

    private static int LiteralBytes(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Bytes => 1 + TreePage.VarintBytes((ulong)literal.BytesValue.Length)
            + literal.BytesValue.Length,
        FilterLiteralKind.Bool => 2,
        FilterLiteralKind.Null => 1,
        _ => 1 + sizeof(ulong),
    };

    private static Span<byte> WriteLiteral(Span<byte> destination, FilterLiteral literal)
    {
        destination[0] = (byte)literal.Kind;
        destination = destination[1..];
        switch (literal.Kind)
        {
            case FilterLiteralKind.Null:
                return destination;
            case FilterLiteralKind.Bool:
                destination[0] = literal.BoolValue ? (byte)1 : (byte)0;
                return destination[1..];
            case FilterLiteralKind.Bytes:
                destination = WriteVarint(destination, (ulong)literal.BytesValue.Length);
                literal.BytesValue.CopyTo(destination);
                return destination[literal.BytesValue.Length..];
            default:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, Bits(literal));
                return destination[sizeof(ulong)..];
        }
    }

    private static ulong Bits(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Signed => unchecked((ulong)literal.SignedValue),
        FilterLiteralKind.Unsigned => literal.UnsignedValue,
        FilterLiteralKind.Float => BitConverter.DoubleToUInt64Bits(literal.FloatValue),
        _ => 0,
    };

    private static FilterLiteral ReadLiteral(ReadOnlySpan<byte> value, ref int at)
    {
        if (at >= value.Length)
        {
            throw new CommitFormatException("A summarised bound ends before its kind.");
        }

        byte kind = value[at++];
        switch ((FilterLiteralKind)kind)
        {
            case FilterLiteralKind.Null:
                return FilterLiteral.Null;
            case FilterLiteralKind.Bool:
                if (at >= value.Length)
                {
                    throw new CommitFormatException("A boolean bound is cut short.");
                }

                return FilterLiteral.From(value[at++] != 0);
            case FilterLiteralKind.Bytes:
                int length = checked((int)ReadVarint(value, ref at));
                if (at + length > value.Length)
                {
                    throw new CommitFormatException("A byte bound runs past its bytes.");
                }

                FilterLiteral literal = FilterLiteral.From(value.Slice(at, length));
                at += length;
                return literal;
            case FilterLiteralKind.Signed:
            case FilterLiteralKind.Unsigned:
            case FilterLiteralKind.Float:
                if (at + sizeof(ulong) > value.Length)
                {
                    throw new CommitFormatException("A numeric bound is cut short.");
                }

                ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value[at..]);
                at += sizeof(ulong);
                return (FilterLiteralKind)kind switch
                {
                    FilterLiteralKind.Signed => FilterLiteral.From(unchecked((long)bits)),
                    FilterLiteralKind.Unsigned => FilterLiteral.From(bits),
                    _ => FilterLiteral.From(BitConverter.UInt64BitsToDouble(bits)),
                };
            default:
                throw new CommitFormatException($"A bound of kind {kind} is not a bound.");
        }
    }

    private static Span<byte> WriteVarint(Span<byte> destination, ulong value) => TreePage.WriteVarint(destination, value);

    private static ulong ReadVarint(ReadOnlySpan<byte> value, ref int at) => TreePage.ReadVarint(value, ref at, "A node's summary");
}
