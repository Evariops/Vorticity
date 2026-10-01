using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// What a node says about its columns, without anything being opened. The encoding is canonical --
/// sorted by path, no field writable two ways, a literal written by its comparison kind rather than
/// its dtype -- because these bytes sit inside a content-addressed page.
/// </summary>
internal sealed class ObjectSummaries : IEquatable<ObjectSummaries>
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

    /// <summary>
    /// The summaries of an open file's columns, reading no data segment: the declared
    /// <paramref name="paths"/>, or the first <paramref name="limit"/> columns when none is given.
    /// </summary>
    public static ObjectSummaries Of(
        VortexFile file, IReadOnlyList<string>? paths = null, int limit = ColumnSummary.DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(file);
        IReadOnlyList<ColumnSummary> summaries = paths is null
            ? ColumnSummaries.Of(file, limit)
            : ColumnSummaries.Of(file, paths);
        return From(summaries);
    }

    /// <summary>
    /// The summaries of a list of columns, in any order, sorted into the canonical order. A column
    /// that says nothing is dropped: it has no encoding to have.
    /// </summary>
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

    /// <summary>
    /// The union of a page's entries' summaries, as an internal entry carries it: the loosest bound
    /// over every part, and silence wherever one part is silent, since a part that says nothing
    /// about a column may hold anything and keeping the others' bound would prune the answer away.
    /// </summary>
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

    /// <summary>
    /// These summaries as bounds only: the same minima and maxima, no longer exact, and a count of
    /// nulls only where it is zero. What an object's summaries become once rows are deleted from it
    /// without a rewrite: a bound over every row is a bound over the live ones, while an exact
    /// extreme or a positive count of nulls may belong to a row that is gone. A column left with
    /// nothing to say, one whose rows were all null, is no longer summarised.
    /// </summary>
    public ObjectSummaries Loosened()
    {
        if (_columns.Length == 0)
        {
            return this;
        }

        List<ColumnSummary> loose = new List<ColumnSummary>(_columns.Length);
        foreach (ColumnSummary held in _columns)
        {
            bool noNulls = held.HasNullCount && held.NullCount == 0;
            ColumnSummary bounded = held with { IsExact = false, HasNullCount = noNulls, NullCount = 0 };
            if (!bounded.IsEmpty)
            {
                loose.Add(bounded);
            }
        }

        return loose.Count == 0 ? None : new ObjectSummaries([.. loose]);
    }

    /// <summary>
    /// Whether a row the predicate selects can lie under this node; false only when these summaries
    /// prove no row can match.
    /// </summary>
    public bool MayMatch(SummaryPruner pruner, long rows)
    {
        ArgumentNullException.ThrowIfNull(pruner);
        return pruner.MayMatch(_columns, rows);
    }

    /// <summary>The summary of one column, when it is carried.</summary>
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

    /// <summary>These summaries' canonical bytes, empty when nothing is summarised.</summary>
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
    /// <exception cref="CommitFormatException">The bytes are not a node's summaries.</exception>
    public static ObjectSummaries FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return None;
        }

        int at = 0;
        int count = Length(ReadVarint(value, ref at), value.Length);
        ColumnSummary[] columns = new ColumnSummary[count];
        for (int i = 0; i < count; i++)
        {
            int length = Length(ReadVarint(value, ref at), value.Length);
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
                // A column that says nothing has no encoding: allowing one would be a second
                // encoding of the same bounds, inside a content-addressed page.
                throw new CommitFormatException($"'{path}' is summarised with no bound and no count.");
            }

            FilterLiteral min = (flags & HasMinFlag) != 0 ? ReadLiteral(value, ref at) : default;
            FilterLiteral max = (flags & HasMaxFlag) != 0 ? ReadLiteral(value, ref at) : default;
            long nulls = (flags & HasNullsFlag) != 0 ? NullCount(ReadVarint(value, ref at)) : 0;
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

    /// <summary>
    /// The summaries of the columns <paramref name="wanted"/> names, out of encoded ones: every other
    /// column is skipped where it lies, its path compared as bytes and nothing of it decoded. What a
    /// walk asks of each node it passes is a pruner's few columns, and a node may summarise many.
    /// The order of the columns is left to <see cref="FromBytes"/> to check.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not a node's summaries.</exception>
    public static ObjectSummaries Of(ReadOnlySpan<byte> value, SummaryColumns wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (value.IsEmpty || wanted.Count == 0)
        {
            return None;
        }

        int at = 0;
        int count = Length(ReadVarint(value, ref at), value.Length);
        ColumnSummary[]? found = null;
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            int length = Length(ReadVarint(value, ref at), value.Length);
            if (at + length >= value.Length)
            {
                throw new CommitFormatException("A summarised column's path runs past its bytes, or ends before its flags.");
            }

            int match = wanted.IndexOf(value.Slice(at, length));
            at += length;
            byte flags = value[at++];
            if ((flags & (HasMinFlag | HasMaxFlag | HasNullsFlag)) == 0)
            {
                throw new CommitFormatException("A summarised column has no bound and no count.");
            }

            if (match < 0)
            {
                SkipLiteral(value, ref at, flags, HasMinFlag);
                SkipLiteral(value, ref at, flags, HasMaxFlag);
                if ((flags & HasNullsFlag) != 0)
                {
                    ReadVarint(value, ref at);
                }

                continue;
            }

            FilterLiteral min = (flags & HasMinFlag) != 0 ? ReadLiteral(value, ref at) : default;
            FilterLiteral max = (flags & HasMaxFlag) != 0 ? ReadLiteral(value, ref at) : default;
            long nulls = (flags & HasNullsFlag) != 0 ? NullCount(ReadVarint(value, ref at)) : 0;
            found ??= new ColumnSummary[Math.Min(count, wanted.Count)];
            if (kept == found.Length)
            {
                throw new CommitFormatException("A node summarises one column twice.");
            }

            found[kept++] = new ColumnSummary(
                wanted.PathAt(match),
                min,
                (flags & HasMinFlag) != 0,
                max,
                (flags & HasMaxFlag) != 0,
                (flags & ExactFlag) != 0,
                nulls,
                (flags & HasNullsFlag) != 0);
        }

        if (at != value.Length)
        {
            throw new CommitFormatException($"A node's summaries have {value.Length - at} bytes left over.");
        }

        if (found is null)
        {
            return None;
        }

        if (kept < found.Length)
        {
            Array.Resize(ref found, kept);
        }

        return new ObjectSummaries(found);
    }

    /// <summary>Moves past a bound the flags say is there.</summary>
    private static void SkipLiteral(ReadOnlySpan<byte> value, ref int at, byte flags, byte which)
    {
        if ((flags & which) == 0)
        {
            return;
        }

        if (at >= value.Length)
        {
            throw new CommitFormatException("A summarised bound ends before its kind.");
        }

        int skip = (FilterLiteralKind)value[at++] switch
        {
            FilterLiteralKind.Null => 0,
            FilterLiteralKind.Bool => 1,
            FilterLiteralKind.Bytes => Length(ReadVarint(value, ref at), value.Length),
            FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Float => sizeof(ulong),
            _ => throw new CommitFormatException($"A bound of kind {value[at - 1]} is not a bound."),
        };
        if (at + skip > value.Length)
        {
            throw new CommitFormatException("A summarised bound is cut short.");
        }

        at += skip;
    }

    /// <summary>A length or a count a varint states, which no summaries of <paramref name="bytes"/> bytes hold more of.</summary>
    private static int Length(ulong value, int bytes) =>
        value > (ulong)bytes ? throw new CommitFormatException("A node's summaries count more than their bytes hold.") : (int)value;

    /// <summary>A count of nulls a varint states, which is not one past a long.</summary>
    private static long NullCount(ulong value) =>
        value > long.MaxValue ? throw new CommitFormatException("A summarised column counts its nulls past a long.") : (long)value;

    /// <summary>
    /// Whether two sets of summaries say the same thing about the same columns. By value, because
    /// entries are compared to decide whether a rebase changed anything, and reference equality
    /// would make a re-applied operation rewrite a page for nothing.
    /// </summary>
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
    /// order. Dropping the bound on a mismatch is the answer that cannot lose a row.
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
                int length = Length(ReadVarint(value, ref at), value.Length);
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

/// <summary>
/// The columns a pruner reads, by their paths and the UTF-8 bytes a node's summaries carry them as:
/// encoded once for a walk, so that each node it passes is matched against them without decoding
/// a path.
/// </summary>
internal sealed class SummaryColumns
{
    private readonly string[] _paths;
    private readonly byte[][] _utf8;

    /// <summary>The columns <paramref name="paths"/> names, each once.</summary>
    internal SummaryColumns(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> distinct = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (!distinct.Contains(path))
            {
                distinct.Add(path);
            }
        }

        _paths = [.. distinct];
        _utf8 = new byte[_paths.Length][];
        for (int i = 0; i < _paths.Length; i++)
        {
            _utf8[i] = Encoding.UTF8.GetBytes(_paths[i]);
        }
    }

    /// <summary>How many columns it names.</summary>
    internal int Count => _paths.Length;

    /// <summary>The path of the <paramref name="index"/>-th column.</summary>
    internal string PathAt(int index) => _paths[index];

    /// <summary>Which column a path's UTF-8 bytes name, or -1 for none of them.</summary>
    internal int IndexOf(ReadOnlySpan<byte> path)
    {
        for (int i = 0; i < _utf8.Length; i++)
        {
            if (path.SequenceEqual(_utf8[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
