using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types.Variants;

namespace Vorticity.Indexes;

/// <summary>Where a path's values of one class lie in an object: what a reader of them reads.</summary>
internal enum PathPlace : byte
{
    /// <summary>In the residual, read by parsing each row's value.</summary>
    Residual = 0,

    /// <summary>In the typed column the path was shredded into.</summary>
    Typed = 1,

    /// <summary>In the values a shredded path kept beside its typed column, of other classes.</summary>
    Value = 2,
}

/// <summary>
/// One path and class of a variant column in an object: how many values of that class the path holds,
/// where they lie, and their bounds.
/// </summary>
/// <param name="Path">The keys from the variant's root, none for the root itself.</param>
/// <param name="Class">The class a question reads them as.</param>
/// <param name="Values">How many values of that class the path holds.</param>
/// <param name="Place">Where they lie.</param>
/// <param name="Low">
/// The least integer or timestamp, the least number's bits, or the booleans seen (1 for false, 2 for
/// true); 0 for the other classes.
/// </param>
/// <param name="High">The greatest integer or timestamp, or the greatest number's bits; 0 otherwise.</param>
/// <param name="NaNs">Whether a number was NaN, which no bound counts.</param>
/// <param name="MinText">The least text cut to the bound, which no text is below; null without text bounds.</param>
/// <param name="MaxText">A text no text is above; null when there is none, as when the greatest was cut and every byte left was the largest.</param>
/// <param name="Groups">
/// The groups of rows of the object the path may hold a value in, a bit each, group 0 the low bit of byte 0;
/// null when it may hold one in every group, or the object counts none.
/// </param>
internal sealed record PathEntry(
    string[] Path, PathClass Class, long Values, PathPlace Place, long Low, long High, bool NaNs, byte[]? MinText, byte[]? MaxText,
    byte[]? Groups = null)
{
    /// <summary>Whether the path may hold a value in group <paramref name="group"/>; any group for -1.</summary>
    internal bool In(int group) => group < 0 || Groups is null || (group >> 3 < Groups.Length && (Groups[group >> 3] & (1 << (group & 7))) != 0);
}

/// <summary>
/// The path directory of a variant column: for every path and class the object's rows hold, its
/// count, its bounds and where it lies, so that a question on a path the object lacks, or on values
/// outside its bounds, refutes the object before any data is read. An entry of the index directory,
/// <see cref="IndexKinds.VariantDirectory"/>, on the variant column, its one run a payload region
/// holding the directory, behind a version byte and the body's length, the body one zstd frame.
/// </summary>
/// <remarks>
/// It is counted from the rows a job parses anyway as it writes an object, so an append,
/// which parses nothing, writes none and drops the one it extends.
/// Each path names the groups of rows it may hold a value in, each <see cref="GroupRows"/> rows from the object's first,
/// which a question on it prunes the others by: a dataset's job names granules, the path's presence (spec E, 3.1).
/// Under the index budget, the paths of the residual held least are left out first, and a directory
/// that left one out refutes nothing by a path's absence. Like every index it is a hint: a region that
/// does not decode refutes nothing.
/// </remarks>
internal sealed class VariantDirectory
{
    // Version 1 named the slices bounded by keys a path lies in, retired with them; 2 names groups of fixed rows.
    private const byte Version = 2;

    private readonly Dictionary<string, PathEntry[]> _byPath;

    internal VariantDirectory(long rows, IReadOnlyList<PathEntry> entries, long groupRows = 0, bool partial = false)
    {
        Rows = rows;
        Entries = entries;
        GroupRows = groupRows;
        Partial = partial;
        Dictionary<string, List<PathEntry>> grouped = new Dictionary<string, List<PathEntry>>(StringComparer.Ordinal);
        foreach (PathEntry entry in entries)
        {
            string key = Joined(entry.Path);
            if (!grouped.TryGetValue(key, out List<PathEntry>? list))
            {
                grouped[key] = list = [];
            }

            list.Add(entry);
        }

        _byPath = new Dictionary<string, PathEntry[]>(grouped.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<PathEntry>> group in grouped)
        {
            _byPath[group.Key] = [.. group.Value];
        }

        _byJoined = _byPath.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    // The same paths, looked up by their keys joined into a span, which a question's filter asks once an object and a
    // predicate: no string made for the asking.
    private readonly Dictionary<string, PathEntry[]>.AlternateLookup<ReadOnlySpan<char>> _byJoined;

    /// <summary>The object's rows.</summary>
    internal long Rows { get; }

    /// <summary>Every path and class, parents before their keys.</summary>
    internal IReadOnlyList<PathEntry> Entries { get; }

    /// <summary>The rows of a group the entries' bits count, the last group fewer; 0 when they name none.</summary>
    internal long GroupRows { get; }

    /// <summary>The groups the entries' bits count; 0 when they name none.</summary>
    internal int GroupCount => GroupRows <= 0 ? 0 : (int)((Rows + GroupRows - 1) / GroupRows);

    /// <summary>Whether the budget left a path out, so that a path's absence proves nothing.</summary>
    internal bool Partial { get; }

    /// <summary>The classes the path of <paramref name="keys"/> holds; empty when the object holds no value there.</summary>
    internal ReadOnlySpan<PathEntry> Of(IReadOnlyList<string> keys) =>
        _byPath.TryGetValue(Joined(keys), out PathEntry[]? entries) ? entries : [];

    /// <summary>
    /// The directory of what <paramref name="census"/> counted, its paths placed by
    /// <paramref name="shredding"/>, the shredding the object's column was written with, or null, and
    /// each named the groups of <paramref name="groupRows"/> rows of the object it may hold a value in, when given.
    /// </summary>
    internal static VariantDirectory From(PathCensus census, VariantShredding? shredding, long groupRows = 0)
    {
        List<PathEntry> entries = [];
        List<string> path = [];
        int groups = groupRows <= 0 ? 0 : (int)((census.Rows + groupRows - 1) / groupRows);
        Walk(census.Root, shredding, groupRows, groups, path, entries);
        return new VariantDirectory(census.Rows, entries, groups > 1 ? groupRows : 0, partial: census.Partial);
    }

    private static void Walk(
        PathCensus.PathCount count, VariantShredding? shredding, long groupRows, int groups, List<string> path, List<PathEntry> entries)
    {
        string[] keys = [.. path];
        byte[]? held = groups > 1 ? GroupsOf(count.Spans, groupRows, groups) : null;
        for (PathClass @class = PathClass.Null; @class <= PathClass.Other; @class++)
        {
            long values = count.Read(@class);
            if (values == 0)
            {
                continue;
            }

            (long low, long high, bool nans, byte[]? min, byte[]? max) = Bounds(count, @class);
            entries.Add(new PathEntry(keys, @class, values, PlaceOf(shredding, @class), low, high, nans, min, max, held));
        }

        // Keys in their ordinal order, so that the same rows give the same bytes.
        List<string> names = [.. count.Keys.Keys];
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            VariantShredding? field = shredding is { Kind: VariantShreddingKind.Object } inner && inner.IndexOf(Encoding.UTF8.GetBytes(name)) is int at and >= 0
                ? inner.Keys[at].Field
                : null;
            path.Add(name);
            Walk(count.Keys[name], field, groupRows, groups, path, entries);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>
    /// A bit for each of the <paramref name="groups"/> groups of <paramref name="groupRows"/> rows one of
    /// <paramref name="spans"/> reaches into; null when every group is reached, or the census kept no spans and the values
    /// may lie anywhere.
    /// </summary>
    private static byte[]? GroupsOf(ReadOnlySpan<long> spans, long groupRows, int groups)
    {
        if (spans.IsEmpty)
        {
            return null;
        }

        byte[] bits = new byte[(groups + 7) / 8];
        int reached = 0;
        for (int i = 0; i < spans.Length; i += 2)
        {
            int first = (int)Math.Min(spans[i] / groupRows, groups - 1);
            int last = (int)Math.Min(spans[i + 1] / groupRows, groups - 1);
            for (int g = first; g <= last; g++)
            {
                if ((bits[g >> 3] & (1 << (g & 7))) == 0)
                {
                    bits[g >> 3] |= (byte)(1 << (g & 7));
                    reached++;
                }
            }
        }

        return reached == groups ? null : bits;
    }

    /// <summary>
    /// This directory within <paramref name="maxBytes"/> bytes: the paths of the residual held least left
    /// out first, and the directory then partial; itself when it fits, or holds no path of the residual.
    /// </summary>
    internal VariantDirectory Within(long maxBytes)
    {
        int bytes = ToBytes().Length;
        if (bytes <= maxBytes)
        {
            return this;
        }

        List<PathEntry> residual = [];
        foreach (PathEntry entry in Entries)
        {
            if (entry.Place == PathPlace.Residual && entry.Path.Length > 0)
            {
                residual.Add(entry);
            }
        }

        if (residual.Count == 0)
        {
            return this;
        }

        // The entries kept in proportion to the bytes, then fewer until they fit.
        residual.Sort(static (a, b) => b.Values.CompareTo(a.Values));
        int kept = (int)(residual.Count * Math.Max(0, (double)maxBytes / bytes));
        while (true)
        {
            HashSet<PathEntry> dropped = [.. residual.GetRange(kept, residual.Count - kept)];
            List<PathEntry> left = [];
            foreach (PathEntry entry in Entries)
            {
                if (!dropped.Contains(entry))
                {
                    left.Add(entry);
                }
            }

            VariantDirectory trimmed = new VariantDirectory(Rows, left, GroupRows, partial: true);
            if (kept == 0 || trimmed.ToBytes().Length <= maxBytes)
            {
                return trimmed;
            }

            kept /= 2;
        }
    }

    /// <summary>Where the values of <paramref name="class"/> lie at a path the object shredded as <paramref name="shredding"/>, or did not.</summary>
    private static PathPlace PlaceOf(VariantShredding? shredding, PathClass @class) => shredding switch
    {
        null => PathPlace.Residual,
        { Kind: VariantShreddingKind.Object } => @class == PathClass.Object ? PathPlace.Typed : PathPlace.Value,
        { Kind: VariantShreddingKind.List } => @class == PathClass.List ? PathPlace.Typed : PathPlace.Value,
        _ => (shredding.Class, @class) switch
        {
            (VariantClass.Integer, PathClass.Integer)
                or (VariantClass.Double, PathClass.Double)
                or (VariantClass.String, PathClass.String)
                or (VariantClass.Boolean, PathClass.Boolean) => PathPlace.Typed,
            _ => PathPlace.Value,
        },
    };

    internal static (long Low, long High, bool NaNs, byte[]? Min, byte[]? Max) Bounds(PathCensus.PathCount count, PathClass @class)
    {
        switch (@class)
        {
            case PathClass.Boolean:
                (bool sawFalse, bool sawTrue) = count.Booleans;
                return ((sawFalse ? 1 : 0) | (sawTrue ? 2 : 0), 0, false, null, null);
            case PathClass.Integer:
                (long min, long max) = count.Integers;
                return (min, max, false, null, null);
            case PathClass.Timestamp:
                (long first, long last) = count.Times;
                return (first, last, false, null, null);
            case PathClass.Double:
            {
                (double least, double greatest, bool nans) = count.Doubles;
                return least > greatest
                    ? (BitConverter.DoubleToInt64Bits(double.NegativeInfinity), BitConverter.DoubleToInt64Bits(double.PositiveInfinity), nans, null, null)
                    : (BitConverter.DoubleToInt64Bits(least), BitConverter.DoubleToInt64Bits(greatest), nans, null, null);
            }

            case PathClass.String:
                return count.HasTextBounds
                    ? (0, 0, false, count.MinText.ToArray(), count.MaxTextCut ? Above(count.MaxText) : count.MaxText.ToArray())
                    : (0, 0, false, null, null);

            default:
                return (0, 0, false, null, null);
        }
    }

    /// <summary>
    /// The least text above every text that starts with <paramref name="prefix"/>: the prefix up to its
    /// last byte below 255, that byte one more; null when every byte is 255.
    /// </summary>
    internal static byte[]? Above(ReadOnlySpan<byte> prefix)
    {
        for (int i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != byte.MaxValue)
            {
                byte[] bound = prefix[..(i + 1)].ToArray();
                bound[i]++;
                return bound;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether some row of the object may satisfy <paramref name="filter"/>, by the paths of the variant
    /// columns it reads: false when a fenced predicate on a path asks for a class the object never holds
    /// there, or for values outside its bounds, under conjunctions and disjunctions. A negation, and
    /// anything that is no predicate on a path, may match.
    /// </summary>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="directories">The directory of each variant column, by its name; a column without one may match anything.</param>
    /// <param name="group">The one group of rows of the object asked about, by the bits its paths carry; -1 for the whole object.</param>
    internal static bool MayMatch(VortexExpr filter, IReadOnlyDictionary<string, VariantDirectory> directories, int group = -1) =>
        VariantFilters.MayMatch(filter, new FencedJudge(directories, group));

    /// <summary>The directories' answer for one fenced predicate.</summary>
    private readonly struct FencedJudge(IReadOnlyDictionary<string, VariantDirectory> directories, int group) : IFencedJudge
    {
        public bool MayMatch(VortexExpr predicate) => MayMatchFenced(predicate, directories, group);
    }

    private static bool MayMatchFenced(VortexExpr predicate, IReadOnlyDictionary<string, VariantDirectory> directories, int group)
    {
        switch (predicate)
        {
            case ComparisonExpr { Field: VariantFieldExpr path } comparison:
                return EntriesOf(path, directories, group, out ReadOnlySpan<PathEntry> entries, out bool partial) is not { } absent
                    || MayMatch(path.Read, entries, comparison.Op, comparison.Value, absent, group, partial);
            case InExpr { Field: VariantFieldExpr path } membership:
            {
                if (EntriesOf(path, directories, group, out ReadOnlySpan<PathEntry> members, out bool trimmed) is not { } lacking)
                {
                    return true;
                }

                foreach (FilterLiteral literal in membership.Literals)
                {
                    if (MayMatch(path.Read, members, ComparisonOp.Equal, literal, lacking, group, trimmed))
                    {
                        return true;
                    }
                }

                return false;
            }

            case NullCheckExpr { Field: VariantFieldExpr path, IsNull: false }:
                return EntriesOf(path, directories, group, out ReadOnlySpan<PathEntry> held, out bool cut) is not { } none
                    || (!none && Holds(path.Read, held, group, cut));
            case StringMatchExpr { Field: VariantFieldExpr path }:
                return EntriesOf(path, directories, group, out ReadOnlySpan<PathEntry> texts, out bool clipped) is not { } empty
                    || (!empty && Holds(path.Read, texts, group, clipped));
            default:
                return true;
        }
    }

    /// <summary>
    /// The entries of <paramref name="path"/>'s column and keys: null when no directory answers for it,
    /// the path takes an index, or the directory is partial and lacks it; otherwise whether the object,
    /// or group <paramref name="group"/> of it, holds no value at the path at all.
    /// </summary>
    private static bool? EntriesOf(
        VariantFieldExpr path, IReadOnlyDictionary<string, VariantDirectory> directories, int group, out ReadOnlySpan<PathEntry> entries, out bool partial)
    {
        entries = default;
        partial = false;
        if (!directories.TryGetValue(path.Column.Path, out VariantDirectory? directory))
        {
            return null;
        }

        partial = directory.Partial;

        // The keys joined as the directory joins them, on the stack while they fit.
        int length = 0;
        foreach (VariantStep step in path.Steps)
        {
            if (!step.IsKey)
            {
                return null;
            }

            length += Digits(step.Key!.Length) + 1 + step.Key.Length;
        }

        char[]? rented = length > 256 ? System.Buffers.ArrayPool<char>.Shared.Rent(length) : null;
        try
        {
            Span<char> joined = rented ?? stackalloc char[256];
            int at = 0;
            foreach (VariantStep step in path.Steps)
            {
                step.Key!.Length.TryFormat(joined[at..], out int written, provider: CultureInfo.InvariantCulture);
                at += written;
                joined[at++] = ':';
                step.Key.CopyTo(joined[at..]);
                at += step.Key.Length;
            }

            entries = directory._byJoined.TryGetValue(joined[..at], out PathEntry[]? found) ? found : [];
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
        if (entries.IsEmpty)
        {
            return directory.Partial ? null : true;
        }

        foreach (PathEntry entry in entries)
        {
            if (entry.In(group))
            {
                return false;
            }
        }

        // A partial directory left some of the path's classes out, whose rows may lie in the group: it says nothing of their
        // absence there.
        return directory.Partial ? null : true;
    }

    /// <summary>
    /// Whether the path holds a value <paramref name="read"/> takes: of its class, or any value for a conversion or a presence.
    /// A <paramref name="partial"/> directory may have left the class out, so it holds one as far as it can tell.
    /// </summary>
    private static bool Holds(VariantRead read, ReadOnlySpan<PathEntry> entries, int group, bool partial) =>
        ClassOf(read) is not { } @class || Find(entries, @class, group) is not null || partial;

    /// <summary>
    /// Whether a value at the path may compare to <paramref name="value"/> by <paramref name="op"/> when read by <paramref name="read"/>.
    /// An entry a directory holds is whole, its bounds those of every value of its class; a <paramref name="partial"/> directory
    /// may have left the class's entry out, which then refutes nothing.
    /// </summary>
    private static bool MayMatch(
        VariantRead read, ReadOnlySpan<PathEntry> entries, ComparisonOp op, FilterLiteral value, bool absent, int group, bool partial)
    {
        if (read == VariantRead.Has)
        {
            // A presence is never null: only asking for the path's presence is refuted by its absence.
            bool asksPresent = value.Kind == FilterLiteralKind.Bool && (op == ComparisonOp.Equal) == value.BoolValue;
            return !asksPresent || !absent;
        }

        if (absent)
        {
            // Every row reads null there, which no comparison matches.
            return false;
        }

        if (ClassOf(read) is not { } @class)
        {
            return true;
        }

        return Find(entries, @class, group) is { } entry ? Within(entry, op, value) : partial;
    }

    /// <summary>The class a read of one class takes, or null for a conversion or a presence.</summary>
    private static PathClass? ClassOf(VariantRead read) => read switch
    {
        VariantRead.AsInt64 => PathClass.Integer,
        VariantRead.AsDouble => PathClass.Double,
        VariantRead.AsString => PathClass.String,
        VariantRead.AsBool => PathClass.Boolean,
        VariantRead.AsTimestamp => PathClass.Timestamp,
        _ => null,
    };

    private static PathEntry? Find(ReadOnlySpan<PathEntry> entries, PathClass @class, int group)
    {
        foreach (PathEntry entry in entries)
        {
            if (entry.Class == @class && entry.In(group))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Whether a value between <paramref name="entry"/>'s bounds may compare to <paramref name="value"/> by <paramref name="op"/>.</summary>
    internal static bool Within(PathEntry entry, ComparisonOp op, FilterLiteral value)
    {
        if (op == ComparisonOp.NotEqual)
        {
            return true;
        }

        switch (entry.Class)
        {
            case PathClass.Integer or PathClass.Timestamp when value.Kind == FilterLiteralKind.Signed:
                return Ordered(entry.Low.CompareTo(value.SignedValue), entry.High.CompareTo(value.SignedValue), op);
            case PathClass.Double when !entry.NaNs && value.Kind is FilterLiteralKind.Float or FilterLiteralKind.Signed:
            {
                double literal = value.Kind == FilterLiteralKind.Float ? value.FloatValue : value.SignedValue;
                if (double.IsNaN(literal))
                {
                    return true;
                }

                return Ordered(
                    BitConverter.Int64BitsToDouble(entry.Low).CompareTo(literal), BitConverter.Int64BitsToDouble(entry.High).CompareTo(literal), op);
            }

            case PathClass.Boolean when value.Kind == FilterLiteralKind.Bool && op == ComparisonOp.Equal:
                return (entry.Low & (value.BoolValue ? 2 : 1)) != 0;
            case PathClass.String when value.Kind == FilterLiteralKind.Bytes && entry.MinText is { } min:
            {
                // The least bound is at or below every text; the greatest, when there is one, above or at.
                int low = min.AsSpan().SequenceCompareTo(value.BytesValue);
                int high = entry.MaxText is { } max ? max.AsSpan().SequenceCompareTo(value.BytesValue) : 1;
                return op switch
                {
                    ComparisonOp.Equal => low <= 0 && high >= 0,
                    ComparisonOp.Less or ComparisonOp.LessOrEqual => low <= 0,
                    ComparisonOp.Greater or ComparisonOp.GreaterOrEqual => high >= 0,
                    _ => true,
                };
            }

            default:
                return true;
        }
    }

    /// <summary>Whether values from a least to a greatest, which compare to the literal as <paramref name="low"/> and <paramref name="high"/>, may compare by <paramref name="op"/>.</summary>
    private static bool Ordered(int low, int high, ComparisonOp op) => op switch
    {
        ComparisonOp.Equal => low <= 0 && high >= 0,
        ComparisonOp.Less => low < 0,
        ComparisonOp.LessOrEqual => low <= 0,
        ComparisonOp.Greater => high > 0,
        ComparisonOp.GreaterOrEqual => high >= 0,
        _ => true,
    };

    /// <summary>The region: behind a version byte and the body's length, the body as one zstd frame.</summary>
    internal byte[] ToBytes() => ZstdRegion.Encode(Version, Body());

    /// <summary>The rows, then each entry: its keys, class, count, place, bounds and texts.</summary>
    private byte[] Body()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint((ulong)Rows);
            writer.WriteVarint((ulong)GroupRows);
            writer.WriteVarint(Partial ? 1UL : 0);
            writer.WriteVarint((ulong)Entries.Count);
            foreach (PathEntry entry in Entries)
            {
                writer.WriteVarint((ulong)entry.Path.Length);
                foreach (string key in entry.Path)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(key);
                    writer.WriteVarint((ulong)utf8.Length);
                    writer.WriteRawBytes(utf8);
                }

                writer.WriteVarint((ulong)entry.Class);
                writer.WriteVarint((ulong)entry.Values);
                writer.WriteVarint((ulong)entry.Place);
                writer.WriteVarint(Zigzag(entry.Low));
                writer.WriteVarint(Zigzag(entry.High));
                writer.WriteVarint(
                    (entry.NaNs ? 1UL : 0) | (entry.MinText is null ? 0 : 2UL) | (entry.MaxText is null ? 0 : 4UL) | (entry.Groups is null ? 0 : 8UL));
                Text(ref writer, entry.MinText);
                Text(ref writer, entry.MaxText);
                Text(ref writer, entry.Groups);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        static void Text(ref ProtoWriter writer, byte[]? text)
        {
            if (text is not null)
            {
                writer.WriteVarint((ulong)text.Length);
                writer.WriteRawBytes(text);
            }
        }
    }

    /// <summary>The directory a region <paramref name="bytes"/> holds, or null when it does not decode.</summary>
    internal static VariantDirectory? From(ReadOnlySpan<byte> bytes) => From(bytes, wanted: null);

    /// <summary>
    /// The directory a region <paramref name="bytes"/> holds, its entries those of <paramref name="wanted"/>'s paths
    /// alone, each a path's keys in UTF-8, or every entry for null; null when it does not decode.
    /// </summary>
    /// <remarks>
    /// The body is decompressed into a rented buffer and every entry is walked as it lies, its keys compared as bytes:
    /// an entry no wanted path names is stepped over with nothing made of it. A question on a path or two asks no more
    /// of a directory (<see cref="Of"/>), where the whole directory of an hour of log records at a thousand a second made
    /// its thousands of entries, their keys and their groups, 4 MB at every question.
    /// </remarks>
    internal static VariantDirectory? From(ReadOnlySpan<byte> bytes, IReadOnlyList<byte[][]>? wanted)
    {
        if (ZstdRegion.Rent(bytes, Version, out int length) is not { } body)
        {
            return null;
        }

        try
        {
            return BodyFrom(body.AsSpan(0, length), wanted);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>The groups of <paramref name="groupRows"/> rows of <paramref name="rows"/>; 0 for none.</summary>
    private static int Groups(long rows, long groupRows) => groupRows <= 0 ? 0 : (int)((rows + groupRows - 1) / groupRows);

    private static VariantDirectory? BodyFrom(ReadOnlySpan<byte> bytes, IReadOnlyList<byte[][]>? wanted)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            long rows = checked((long)reader.ReadVarint());
            long groupRows = checked((long)reader.ReadVarint());
            bool partial = reader.ReadVarint() != 0;
            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            List<PathEntry> entries = new List<PathEntry>(wanted is null ? (int)count : 4);
            Span<int> stack = stackalloc int[32];
            for (ulong i = 0; i < count; i++)
            {
                ulong depth = reader.ReadVarint();
                if (depth > (ulong)bytes.Length)
                {
                    return null;
                }

                // Where each key lies, a start and a length, compared to the wanted paths before any is made a string.
                Span<int> keys = depth <= 16 ? stack[..(2 * (int)depth)] : new int[2 * (int)depth];
                for (int k = 0; k < (int)depth; k++)
                {
                    ReadOnlySpan<byte> key = reader.ReadLengthDelimited();
                    int end = bytes.Length - reader.Remaining;
                    keys[2 * k] = end - key.Length;
                    keys[(2 * k) + 1] = key.Length;
                }

                ulong @class = reader.ReadVarint();
                long values = checked((long)reader.ReadVarint());
                ulong place = reader.ReadVarint();
                long low = Unzigzag(reader.ReadVarint());
                long high = Unzigzag(reader.ReadVarint());
                ulong flags = reader.ReadVarint();
                ReadOnlySpan<byte> min = (flags & 2) != 0 ? reader.ReadLengthDelimited() : default;
                ReadOnlySpan<byte> max = (flags & 4) != 0 ? reader.ReadLengthDelimited() : default;
                ReadOnlySpan<byte> held = (flags & 8) != 0 ? reader.ReadLengthDelimited() : default;
                if (@class > (ulong)PathClass.Other || place > (ulong)PathPlace.Value || ((flags & 8) != 0 && held.Length != (Groups(rows, groupRows) + 7) / 8))
                {
                    return null;
                }

                if (wanted is not null && !Wanted(bytes, keys, wanted))
                {
                    continue;
                }

                string[] path = new string[(int)depth];
                for (int k = 0; k < path.Length; k++)
                {
                    path[k] = Encoding.UTF8.GetString(bytes.Slice(keys[2 * k], keys[(2 * k) + 1]));
                }

                entries.Add(new PathEntry(
                    path, (PathClass)@class, values, (PathPlace)place, low, high, (flags & 1) != 0,
                    (flags & 2) != 0 ? min.ToArray() : null, (flags & 4) != 0 ? max.ToArray() : null, (flags & 8) != 0 ? held.ToArray() : null));
            }

            return reader.End ? new VariantDirectory(rows, entries, groupRows, partial) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Whether the keys at <paramref name="keys"/>' places in <paramref name="bytes"/> are those of a path of <paramref name="wanted"/>.</summary>
    private static bool Wanted(ReadOnlySpan<byte> bytes, ReadOnlySpan<int> keys, IReadOnlyList<byte[][]> wanted)
    {
        int depth = keys.Length / 2;

        // By index: a foreach over the list boxes its enumerator, once for each entry of the directory.
        for (int w = 0; w < wanted.Count; w++)
        {
            byte[][] path = wanted[w];
            if (path.Length != depth)
            {
                continue;
            }

            int k = 0;
            while (k < depth && bytes.Slice(keys[2 * k], keys[(2 * k) + 1]).SequenceEqual(path[k]))
            {
                k++;
            }

            if (k == depth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The directory of the variant column at top-level field <paramref name="field"/> of
    /// <paramref name="file"/>; null when the file carries none, or one that does not decode.
    /// </summary>
    internal static ValueTask<(VariantDirectory? Directory, int Segments, long Bytes)> ReadAsync(
        VortexFile file, int field, CancellationToken cancellationToken) =>
        ReadAsync(file, field, wanted: null, cancellationToken);

    /// <summary>
    /// The directory of the variant column at top-level field <paramref name="field"/> of <paramref name="file"/>, its
    /// entries those of <paramref name="wanted"/>'s paths alone (<see cref="From(ReadOnlySpan{byte}, IReadOnlyList{byte[][]})"/>),
    /// or every entry for null; null when the file carries none, or one that does not decode.
    /// </summary>
    internal static async ValueTask<(VariantDirectory? Directory, int Segments, long Bytes)> ReadAsync(
        VortexFile file, int field, IReadOnlyList<byte[][]>? wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, 0, 0);
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.VariantDirectory || entry.ColumnPath is not [uint column] || column != (uint)field
                || entry.Runs is not [IndexRun { Payload: [IndexSegment region] }])
            {
                continue;
            }

            using Vorticity.IO.SegmentRequestSet requests = new Vorticity.IO.SegmentRequestSet(1);
            int slot = requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> bytes = requests.GetBuffer(slot).Span;
            return (region.Holds(bytes) ? From(bytes, wanted) : null, 1, region.Length);
        }

        return (null, 0, 0);
    }

    /// <summary>The decimal digits of <paramref name="value"/>, not negative.</summary>
    private static int Digits(int value)
    {
        int digits = 1;
        for (; value >= 10; value /= 10)
        {
            digits++;
        }

        return digits;
    }

    private static string Joined(IReadOnlyList<string> keys)
    {
        StringBuilder joined = new StringBuilder();
        foreach (string key in keys)
        {
            // A key's length before it, so that no two paths join alike.
            joined.Append(key.Length).Append(':').Append(key);
        }

        return joined.ToString();
    }

    private static ulong Zigzag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static long Unzigzag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
