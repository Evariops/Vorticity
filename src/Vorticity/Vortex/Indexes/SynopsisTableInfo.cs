using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Indexes;

/// <summary>What a synopsis table holds: the states of runs, of coarser buckets, or of one column's values.</summary>
internal enum SynopsisTableKind : byte
{
    /// <summary>Per slice, per combination of the first synopsis columns: the run statistics.</summary>
    Runs = 0,

    /// <summary>Per bucket coarser than a slice, per combination of the first synopsis columns.</summary>
    Coarse = 1,

    /// <summary>Per bucket, per value of one synopsis column.</summary>
    Values = 2,
}

/// <summary>The bucket a table's rows count over: a slice, an hour of data time, or the whole object.</summary>
internal enum SynopsisBucket : byte
{
    Slice = 0,
    Hour = 1,
    Object = 2,
}

/// <summary>
/// One table of an object's synopsis, as its index entry describes it: its kind, its bucket, the fields that
/// key its rows, the measure fields whose states each row holds, and its rows. The rows themselves are an
/// array payload of the entry's one run: a struct of the bucket's start, the keys, a count, then each
/// measure's count of values, its exact sum in the words its state lays out, its least and its greatest.
/// </summary>
/// <param name="Kind">What the table holds.</param>
/// <param name="Bucket">What its rows count over.</param>
/// <param name="Keys">The fields that key its rows.</param>
/// <param name="Measures">The fields whose states its rows hold.</param>
/// <param name="Rows">How many rows.</param>
internal sealed record SynopsisTableInfo(SynopsisTableKind Kind, SynopsisBucket Bucket, int[] Keys, int[] Measures, int Rows)
{
    private const byte Version = 1;

    /// <summary>The entry's options: the kind, the bucket, the keys, the measures and the rows, behind a version byte.</summary>
    internal byte[] OptionBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((byte)Kind);
            writer.WriteVarint((byte)Bucket);
            writer.WriteVarint((ulong)Keys.Length);
            foreach (int key in Keys)
            {
                writer.WriteVarint((ulong)key);
            }

            writer.WriteVarint((ulong)Measures.Length);
            foreach (int measure in Measures)
            {
                writer.WriteVarint((ulong)measure);
            }

            writer.WriteVarint((ulong)Rows);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The struct of the table's rows over a file of <paramref name="schema"/>, whose synopsis paths are
    /// <paramref name="paths"/>, built in <paramref name="types"/>: the bucket, each key at its field's type
    /// or its path's class, the count, then per measure its count of values, its sum's words (two for an
    /// integer, five for a float), its least and its greatest, all 64-bit. The writer builds its rows at
    /// this type, and a reader decodes them at it.
    /// </summary>
    internal DType RowType(DType schema, IReadOnlyList<SynopsisPath> paths, DTypeArena types)
    {
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        List<string> names = ["bucket"];
        List<DType> fields = [longs];
        foreach (int key in Keys)
        {
            names.Add(NameOf(key, schema, paths));
            fields.Add(SynopsisPath.Names(key)
                ? ClassType(paths[SynopsisPath.Place(key)].Class, types)
                : VortexTypes.ToDType(VortexTypes.FromDType(schema.GetField(key)), types));
        }

        names.Add("count");
        fields.Add(longs);
        foreach (int measure in Measures)
        {
            string name = NameOf(measure, schema, paths);
            bool floats;
            if (SynopsisPath.Names(measure))
            {
                floats = paths[SynopsisPath.Place(measure)].Class == Types.Variants.VariantClass.Double;
            }
            else
            {
                DType dtype = schema.GetField(measure);
                while (dtype.Kind == DTypeKind.Extension)
                {
                    dtype = dtype.StorageType;
                }

                floats = dtype.PType.IsFloat();
            }

            string[] words = floats ? ["m0", "m1", "m2", "meta", "high"] : ["low", "high"];
            names.Add($"{name}.values");
            foreach (string word in words)
            {
                names.Add($"{name}.sum.{word}");
            }

            names.Add($"{name}.min");
            names.Add($"{name}.max");
            for (int i = 0; i < words.Length + 3; i++)
            {
                fields.Add(longs);
            }
        }

        return types.Struct([.. names], [.. fields], Nullability.NonNullable);
    }

    /// <summary>The name a key or measure <paramref name="entry"/> takes in a table: its field's, or its path's, <c>field["key"]</c>.</summary>
    internal static string NameOf(int entry, DType schema, IReadOnlyList<SynopsisPath> paths) =>
        SynopsisPath.Names(entry) && paths[SynopsisPath.Place(entry)] is { } path
            ? $"{schema.GetFieldName(path.Field)}[\"{path.Key}\"]"
            : schema.GetFieldName(entry);

    /// <summary>The nullable type a path's values of <paramref name="class"/> are read at: the typed column the shredder builds for it.</summary>
    internal static DType ClassType(Types.Variants.VariantClass @class, DTypeArena types) => @class switch
    {
        Types.Variants.VariantClass.Integer => types.Primitive(PType.I64, Nullability.Nullable),
        Types.Variants.VariantClass.Double => types.Primitive(PType.F64, Nullability.Nullable),
        Types.Variants.VariantClass.String => types.Utf8(Nullability.Nullable),
        _ => types.Bool(Nullability.Nullable),
    };

    /// <summary>The table the options <paramref name="bytes"/> describe, or null when they do not decode.</summary>
    internal static SynopsisTableInfo? From(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong kind = reader.ReadVarint();
            ulong bucket = reader.ReadVarint();
            int[]? keys = Fields(ref reader, bytes.Length);
            int[]? measures = keys is null ? null : Fields(ref reader, bytes.Length);
            int rows = checked((int)reader.ReadVarint());
            return measures is not null && reader.End && kind <= (ulong)SynopsisTableKind.Values && bucket <= (ulong)SynopsisBucket.Object
                ? new SynopsisTableInfo((SynopsisTableKind)kind, (SynopsisBucket)bucket, keys!, measures, rows)
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    private static int[]? Fields(ref ProtoReader reader, int bound)
    {
        ulong count = reader.ReadVarint();
        if (count > (ulong)bound)
        {
            return null;
        }

        int[] fields = new int[(int)count];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = checked((int)reader.ReadVarint());
        }

        return fields;
    }

    /// <summary>
    /// Which of <paramref name="tables"/> the synopsis keeps, and why, with the numbers: a greedy choice in
    /// the manner of the classic selection of views. The questions are the grains the tables hold, a bucket
    /// and keys each, weighed alike. Answering one costs its source's bytes at <paramref name="prices"/>'s
    /// price of a byte, and its rows at the price of a row grouped: the columns it sums, whose bytes
    /// <paramref name="columnBytes"/> gives, over the object's <paramref name="rows"/>, or the cheapest table
    /// kept that answers it by merging its rows. Each round keeps the table that lowers the questions'
    /// costs the most per byte it holds, as long as it lowers them and fits what is left of
    /// <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// A table answers a question when its bucket is as fine or finer, a slice's nesting in its hour and an
    /// hour's in the object, its keys hold the question's, and its measures too. Of two tables that hold the
    /// same rows, the one weighed second saves nothing, and is left out.
    /// </remarks>
    internal static (bool[] Kept, string[] Verdicts) Choose(
        IReadOnlyList<(SynopsisTableInfo Table, long Bytes)> tables, Func<int, long> columnBytes, long rows, long budget, SynopsisPrices prices)
    {
        int count = tables.Count;

        // The questions, a grain each, and what each costs now: its columns.
        List<SynopsisTableInfo> questions = [];
        int[] asks = new int[count];
        for (int c = 0; c < count; c++)
        {
            SynopsisTableInfo table = tables[c].Table;
            asks[c] = questions.FindIndex(q => q.Bucket == table.Bucket && q.Keys.Length == table.Keys.Length && Holds(q.Keys, table.Keys));
            if (asks[c] < 0)
            {
                asks[c] = questions.Count;
                questions.Add(table);
            }
        }

        double[] now = new double[questions.Count];
        for (int q = 0; q < now.Length; q++)
        {
            long bytes = 0;
            foreach (int field in questions[q].Keys)
            {
                bytes += columnBytes(field);
            }

            foreach (int field in questions[q].Measures)
            {
                bytes += columnBytes(field);
            }

            now[q] = prices.Cost(bytes, rows);
        }

        bool[] kept = new bool[count];
        string[] verdicts = new string[count];
        long left = budget;
        while (true)
        {
            int pick = -1;
            double pickSaved = 0;
            double pickRatio = 0;
            int pickQuestions = 0;
            for (int c = 0; c < count; c++)
            {
                if (kept[c] || tables[c].Bytes > left)
                {
                    continue;
                }

                (double saved, int answered) = Saved(tables[c], questions, now, prices);
                double ratio = saved / Math.Max(tables[c].Bytes, 1);
                if (saved > 0 && ratio > pickRatio)
                {
                    (pick, pickSaved, pickRatio, pickQuestions) = (c, saved, ratio, answered);
                }
            }

            if (pick < 0)
            {
                break;
            }

            kept[pick] = true;
            left -= tables[pick].Bytes;
            verdicts[pick] = FormattableString.Invariant(
                $"kept: saves {pickSaved * 1e3:G4} ms over {pickQuestions} questions for {tables[pick].Bytes} bytes, {left} bytes of the budget left");
            double own = prices.Cost(tables[pick].Bytes, tables[pick].Table.Rows);
            for (int q = 0; q < questions.Count; q++)
            {
                if (tables[pick].Table.Answers(questions[q]))
                {
                    now[q] = Math.Min(now[q], own);
                }
            }
        }

        for (int c = 0; c < count; c++)
        {
            if (!kept[c])
            {
                (double saved, int _) = Saved(tables[c], questions, now, prices);
                verdicts[c] = saved <= 0
                    ? FormattableString.Invariant($"left out: its {tables[c].Bytes} bytes save nothing the columns and the tables kept do not")
                    : FormattableString.Invariant($"left out: {tables[c].Bytes} bytes past the {left} the budget of {budget} left");
            }
        }

        return (kept, verdicts);
    }

    /// <summary>What answering from <paramref name="table"/> saves over the questions it answers, against what they cost <paramref name="now"/>, and how many it lowers.</summary>
    private static (double Saved, int Answered) Saved(
        (SynopsisTableInfo Table, long Bytes) table, List<SynopsisTableInfo> questions, double[] now, SynopsisPrices prices)
    {
        double own = prices.Cost(table.Bytes, table.Table.Rows);
        double saved = 0;
        int answered = 0;
        for (int q = 0; q < questions.Count; q++)
        {
            if (table.Table.Answers(questions[q]) && now[q] > own)
            {
                saved += now[q] - own;
                answered++;
            }
        }

        return (saved, answered);
    }

    /// <summary>Whether this table's rows give <paramref name="other"/>'s, merged: a bucket as fine or finer, and its keys and measures.</summary>
    private bool Answers(SynopsisTableInfo other) =>
        Bucket <= other.Bucket && Holds(Keys, other.Keys) && Holds(Measures, other.Measures);

    private static bool Holds(int[] fields, int[] others)
    {
        foreach (int field in others)
        {
            if (Array.IndexOf(fields, field) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every synopsis table of <paramref name="file"/>, their regions read together, each table's rows
    /// decoded over the file's encodings; those whose region does not hold its checksum, or does not
    /// decode, left out.
    /// </summary>
    internal static async ValueTask<SynopsisTableSet> ReadAsync(VortexFile file, CancellationToken cancellationToken)
    {
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        List<(SynopsisTableInfo Table, IndexSegment Region, byte[] DType)> found = [];
        foreach (IndexEntry entry in directory?.Entries ?? [])
        {
            // The tables lie in the file itself: an append drops them.
            if (entry.Kind == IndexKinds.SynopsisTable && From(entry.Options) is { } table
                && entry.Runs is [IndexRun { Origin: 0, Payload: [IndexSegment region], PayloadDTypes: [byte[] dtype] }])
            {
                found.Add((table, region, dtype));
            }
        }

        if (found.Count == 0)
        {
            return new SynopsisTableSet(new ScanContext([]), []);
        }

        DTypeArena types = new DTypeArena();
        return await DecodeAsync(
            file.Segments, file.CreateIndexContext(0), [.. found.Select(f => (f.Table, f.Region))], (_, i) => DTypeFlatBuffers.Read(found[i].DType, types), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The synopsis tables of a file its table of contents <paramref name="contents"/> names, read from
    /// <paramref name="source"/> without an open: those <paramref name="wanted"/> keeps, every one when it is
    /// null, their regions in one request set, each table's rows decoded at its <see cref="RowType"/> over
    /// the file's schema, which the contents hold.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static async ValueTask<SynopsisTableSet> ReadAsync(
        IO.ISegmentReader source, SynopsisContents contents, Func<SynopsisTableInfo, bool>? wanted, VortexReadOptions? options,
        CancellationToken cancellationToken)
    {
        DType schema = contents.SchemaType;
        List<(SynopsisTableInfo, IndexSegment)> found = [];
        foreach ((SynopsisTableInfo table, IndexSegment region) in contents.Tables)
        {
            if (wanted?.Invoke(table) ?? true)
            {
                found.Add((table, region));
            }
        }

        DTypeArena types = new DTypeArena();
        string[] encodings = [.. contents.Encodings];
        SynopsisTableSet set = await DecodeAsync(
            source, new ScanContext(encodings, options ?? VortexReadOptions.Default), found, (table, _) => table.RowType(schema, contents.Slices.Paths, types), cancellationToken)
            .ConfigureAwait(false);
        set.Types = types;
        return set;
    }

    /// <summary>
    /// Reads the regions of <paramref name="found"/> in one request set and decodes each into
    /// <paramref name="context"/>'s arena at the dtype <paramref name="dtypeOf"/> gives it; a region that
    /// does not hold its checksum, or does not decode, is left out.
    /// </summary>
    private static async ValueTask<SynopsisTableSet> DecodeAsync(
        IO.ISegmentReader source, ScanContext context, List<(SynopsisTableInfo Table, IndexSegment Region)> found,
        Func<SynopsisTableInfo, int, DType> dtypeOf, CancellationToken cancellationToken)
    {
        try
        {
            using IO.SegmentRequestSet requests = new IO.SegmentRequestSet(Math.Max(found.Count, 1));
            int[] slots = new int[found.Count];
            for (int i = 0; i < found.Count; i++)
            {
                IndexSegment region = found[i].Region;
                slots[i] = requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            }

            List<(SynopsisTableInfo, int)> tables = [];
            if (found.Count == 0)
            {
                return new SynopsisTableSet(context, tables);
            }

            await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < found.Count; i++)
            {
                (SynopsisTableInfo table, IndexSegment region) = found[i];
                Buffers.VortexBuffer bytes = requests.GetBuffer(slots[i]);
                if (!region.Holds(bytes.Span))
                {
                    continue;
                }

                try
                {
                    // The rows copied into the arena: the bytes read go back with the requests.
                    context.Decode.LoadBlob(bytes);
                    ArrayNode root = context.Nodes.Root;
                    int node = context.Decode.DecodeRoot(in root, dtypeOf(table, i), table.Rows);
                    tables.Add((table, context.Canonical.CopyFrom(context.Canonical, node)));
                }
                catch (VortexFormatException)
                {
                }
            }

            return new SynopsisTableSet(context, tables);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }
}

/// <summary>
/// What the synopsis's choice prices a question's source at: each byte it reads, and each row it groups.
/// </summary>
/// <param name="ByteSeconds">The seconds a byte read takes.</param>
/// <param name="RowSeconds">The seconds grouping a row takes, a row of data or of a table.</param>
internal readonly record struct SynopsisPrices(double ByteSeconds, double RowSeconds)
{
    /// <summary>
    /// A byte at a tenth of a gigabyte a second, an object store's one stream as the encoding advice takes
    /// it, provisional until the bench of questions measures each store's and the local cache's; and a row
    /// grouped in 12 ns, the engine's on one lane over three keys, a count and three sums, reading and
    /// decoding included (`vortex-queries --micro synopsistables`).
    /// </summary>
    internal static SynopsisPrices Default { get; } = new SynopsisPrices(1e-8, 12e-9);

    /// <summary>What reading <paramref name="bytes"/> and grouping <paramref name="rows"/> costs, in seconds.</summary>
    internal double Cost(long bytes, long rows) => (bytes * ByteSeconds) + (rows * RowSeconds);
}

/// <summary>An object's synopsis tables as read: each table, its rows a node of <see cref="Arena"/>, which disposing lets go.</summary>
internal sealed class SynopsisTableSet : IDisposable
{
    private readonly ScanContext _context;

    internal SynopsisTableSet(ScanContext context, IReadOnlyList<(SynopsisTableInfo Table, int Node)> tables)
    {
        _context = context;
        Tables = tables;
    }

    /// <summary>Each table, and its rows.</summary>
    internal IReadOnlyList<(SynopsisTableInfo Table, int Node)> Tables { get; }

    /// <summary>The arena the rows lie in.</summary>
    internal CanonicalArena Arena => _context.Canonical;

    /// <summary>The arena of the rows' dtypes, where a struct over their columns is built; null when the reader does not say.</summary>
    internal DTypeArena? Types { get; set; }

    public void Dispose() => _context.Dispose();
}
