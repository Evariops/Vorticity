using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
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
internal sealed record PathEntry(
    string[] Path, PathClass Class, long Values, PathPlace Place, long Low, long High, bool NaNs, byte[]? MinText, byte[]? MaxText);

/// <summary>
/// The path directory of a variant column: for every path and class the object's rows hold, its
/// count, its bounds and where it lies, so that a question on a path the object lacks, or on values
/// outside its bounds, refutes the object before any data is read. An entry of the index directory,
/// <see cref="IndexKinds.VariantDirectory"/>, on the variant column, its one run a payload region
/// holding the directory, behind a version byte and the body's length, the body one zstd frame.
/// </summary>
/// <remarks>
/// It is counted from the rows a job of a dataset of periods parses anyway, in the first pass that
/// cuts its slices, so an append, which parses nothing, writes none and drops the one it extends.
/// Like every index it is a hint: a region that does not decode refutes nothing.
/// </remarks>
internal sealed class VariantDirectory
{
    private const byte Version = 1;

    private readonly Dictionary<string, PathEntry[]> _byPath;

    internal VariantDirectory(long rows, IReadOnlyList<PathEntry> entries)
    {
        Rows = rows;
        Entries = entries;
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
    }

    /// <summary>The object's rows.</summary>
    internal long Rows { get; }

    /// <summary>Every path and class, parents before their keys.</summary>
    internal IReadOnlyList<PathEntry> Entries { get; }

    /// <summary>The classes the path of <paramref name="keys"/> holds; empty when the object holds no value there.</summary>
    internal ReadOnlySpan<PathEntry> Of(IReadOnlyList<string> keys) =>
        _byPath.TryGetValue(Joined(keys), out PathEntry[]? entries) ? entries : [];

    /// <summary>
    /// The directory of what <paramref name="census"/> counted, its paths placed by
    /// <paramref name="shredding"/>, the shredding the object's column was written with, or null.
    /// </summary>
    internal static VariantDirectory From(PathCensus census, VariantShredding? shredding)
    {
        List<PathEntry> entries = [];
        List<string> path = [];
        Walk(census.Root, shredding, path, entries);
        return new VariantDirectory(census.Rows, entries);
    }

    private static void Walk(PathCensus.PathCount count, VariantShredding? shredding, List<string> path, List<PathEntry> entries)
    {
        string[] keys = [.. path];
        for (PathClass @class = PathClass.Null; @class <= PathClass.Other; @class++)
        {
            long values = count.Read(@class);
            if (values == 0)
            {
                continue;
            }

            (long low, long high, bool nans, byte[]? min, byte[]? max) = Bounds(count, @class);
            entries.Add(new PathEntry(keys, @class, values, PlaceOf(shredding, @class), low, high, nans, min, max));
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
            Walk(count.Keys[name], field, path, entries);
            path.RemoveAt(path.Count - 1);
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

    private static (long Low, long High, bool NaNs, byte[]? Min, byte[]? Max) Bounds(PathCensus.PathCount count, PathClass @class)
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
    private static byte[]? Above(ReadOnlySpan<byte> prefix)
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
    internal static bool MayMatch(VortexExpr filter, IReadOnlyDictionary<string, VariantDirectory> directories) => filter switch
    {
        LogicalExpr { IsAnd: true } both => MayMatch(both.Left, directories) && MayMatch(both.Right, directories),
        LogicalExpr either => MayMatch(either.Left, directories) || MayMatch(either.Right, directories),
        VariantFilterExpr fence => MayMatchFenced(fence.Inner, directories),
        _ => true,
    };

    private static bool MayMatchFenced(VortexExpr predicate, IReadOnlyDictionary<string, VariantDirectory> directories)
    {
        switch (predicate)
        {
            case ComparisonExpr { Field: VariantFieldExpr path } comparison:
                return EntriesOf(path, directories, out ReadOnlySpan<PathEntry> entries) is not { } absent
                    || MayMatch(path.Read, entries, comparison.Op, comparison.Value, absent);
            case InExpr { Field: VariantFieldExpr path } membership:
            {
                if (EntriesOf(path, directories, out ReadOnlySpan<PathEntry> members) is not { } lacking)
                {
                    return true;
                }

                foreach (FilterLiteral literal in membership.Literals)
                {
                    if (MayMatch(path.Read, members, ComparisonOp.Equal, literal, lacking))
                    {
                        return true;
                    }
                }

                return false;
            }

            case NullCheckExpr { Field: VariantFieldExpr path, IsNull: false }:
                return EntriesOf(path, directories, out ReadOnlySpan<PathEntry> held) is not { } none || (!none && Holds(path.Read, held));
            case StringMatchExpr { Field: VariantFieldExpr path }:
                return EntriesOf(path, directories, out ReadOnlySpan<PathEntry> texts) is not { } empty || (!empty && Holds(path.Read, texts));
            default:
                return true;
        }
    }

    /// <summary>
    /// The entries of <paramref name="path"/>'s column and keys: null when no directory answers for it,
    /// or the path takes an index; otherwise whether the object holds no value at the path at all.
    /// </summary>
    private static bool? EntriesOf(
        VariantFieldExpr path, IReadOnlyDictionary<string, VariantDirectory> directories, out ReadOnlySpan<PathEntry> entries)
    {
        entries = default;
        if (!directories.TryGetValue(path.Column.Path, out VariantDirectory? directory))
        {
            return null;
        }

        string[] keys = new string[path.Steps.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (!path.Steps[i].IsKey)
            {
                return null;
            }

            keys[i] = path.Steps[i].Key!;
        }

        entries = directory.Of(keys);
        return entries.IsEmpty;
    }

    /// <summary>Whether the path holds a value <paramref name="read"/> takes: of its class, or any value for a conversion or a presence.</summary>
    private static bool Holds(VariantRead read, ReadOnlySpan<PathEntry> entries) =>
        ClassOf(read) is not { } @class || Find(entries, @class) is not null;

    /// <summary>Whether a value at the path may compare to <paramref name="value"/> by <paramref name="op"/> when read by <paramref name="read"/>.</summary>
    private static bool MayMatch(VariantRead read, ReadOnlySpan<PathEntry> entries, ComparisonOp op, FilterLiteral value, bool absent)
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

        return Find(entries, @class) is { } entry && Within(entry, op, value);
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

    private static PathEntry? Find(ReadOnlySpan<PathEntry> entries, PathClass @class)
    {
        foreach (PathEntry entry in entries)
        {
            if (entry.Class == @class)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Whether a value between <paramref name="entry"/>'s bounds may compare to <paramref name="value"/> by <paramref name="op"/>.</summary>
    private static bool Within(PathEntry entry, ComparisonOp op, FilterLiteral value)
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
    internal byte[] ToBytes()
    {
        byte[] body = Body();
        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(body.Length)];
        ZstdCompressor encoder = Writing.ZstdEncoders.Rent();
        int written;
        try
        {
            if (encoder.Compress(body, frame, out _, out written) != System.Buffers.OperationStatus.Done)
            {
                throw new InvalidOperationException("A path directory did not compress into the bound zstd gives it.");
            }
        }
        finally
        {
            Writing.ZstdEncoders.Return(encoder);
        }

        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)body.Length);
            writer.WriteRawBytes(frame.AsSpan(0, written));
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The rows, then each entry: its keys, class, count, place, bounds and texts.</summary>
    private byte[] Body()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint((ulong)Rows);
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
                writer.WriteVarint((entry.NaNs ? 1UL : 0) | (entry.MinText is null ? 0 : 2UL) | (entry.MaxText is null ? 0 : 4UL));
                Text(ref writer, entry.MinText);
                Text(ref writer, entry.MaxText);
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
    internal static VariantDirectory? From(ReadOnlySpan<byte> bytes)
    {
        byte[] body;
        try
        {
            ProtoReader header = new ProtoReader(bytes);
            if (header.ReadVarint() != Version)
            {
                return null;
            }

            // A body of more than a thousand times its frame is not one this writer made.
            ulong length = header.ReadVarint();
            ReadOnlySpan<byte> frame = bytes[(bytes.Length - header.Remaining)..];
            if (length > (ulong)frame.Length * 1_000 || length > int.MaxValue)
            {
                return null;
            }

            body = new byte[(int)length];
            ZstdDecompressor decoder = ZstdDecoders.Rent();
            try
            {
                if (decoder.Decompress(frame, body, out _, out int produced) != System.Buffers.OperationStatus.Done || produced != body.Length)
                {
                    return null;
                }
            }
            finally
            {
                ZstdDecoders.Return(decoder);
            }
        }
        catch (VortexFormatException)
        {
            return null;
        }

        return BodyFrom(body);
    }

    private static VariantDirectory? BodyFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            long rows = checked((long)reader.ReadVarint());
            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            PathEntry[] entries = new PathEntry[(int)count];
            for (int i = 0; i < entries.Length; i++)
            {
                ulong depth = reader.ReadVarint();
                if (depth > (ulong)bytes.Length)
                {
                    return null;
                }

                string[] path = new string[(int)depth];
                for (int k = 0; k < path.Length; k++)
                {
                    path[k] = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                }

                ulong @class = reader.ReadVarint();
                long values = checked((long)reader.ReadVarint());
                ulong place = reader.ReadVarint();
                long low = Unzigzag(reader.ReadVarint());
                long high = Unzigzag(reader.ReadVarint());
                ulong flags = reader.ReadVarint();
                byte[]? min = (flags & 2) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                byte[]? max = (flags & 4) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                if (@class > (ulong)PathClass.Other || place > (ulong)PathPlace.Value)
                {
                    return null;
                }

                entries[i] = new PathEntry(path, (PathClass)@class, values, (PathPlace)place, low, high, (flags & 1) != 0, min, max);
            }

            return reader.End ? new VariantDirectory(rows, entries) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The directory of the variant column at top-level field <paramref name="field"/> of
    /// <paramref name="file"/>; null when the file carries none, or one that does not decode.
    /// </summary>
    internal static async ValueTask<(VariantDirectory? Directory, int Segments, long Bytes)> ReadAsync(
        VortexFile file, int field, CancellationToken cancellationToken)
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
            return (region.Holds(bytes) ? From(bytes) : null, 1, region.Length);
        }

        return (null, 0, 0);
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
