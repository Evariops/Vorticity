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

    /// <summary>
    /// Per bucket coarser than a slice, per combination of the first synopsis columns, as <see cref="Coarse"/>, each
    /// measure's distribution sketched besides its states, on the grid of <see cref="SynopsisTableInfo.SketchBits"/>.
    /// </summary>
    Sketches = 3,
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
/// measure's count of values, its exact sum in the words its state lays out, its least and its greatest; a table
/// of sketches then holds each measure's sketch (<see cref="Aggregating.QuantileSketch.Encode"/>), and the distinct
/// sketch of each key it names deeper than its own (<see cref="Aggregating.CardinalitySketch.Encode"/>).
/// </summary>
/// <param name="Kind">What the table holds.</param>
/// <param name="Bucket">What its rows count over.</param>
/// <param name="Keys">The fields that key its rows.</param>
/// <param name="Measures">The fields whose states its rows hold.</param>
/// <param name="Rows">How many rows.</param>
internal sealed record SynopsisTableInfo(SynopsisTableKind Kind, SynopsisBucket Bucket, int[] Keys, int[] Measures, int Rows)
{
    private const byte Version = 1;

    /// <summary>The version of the options of a table that holds distinct sketches, which say their keys after the measures.</summary>
    private const byte DistinctVersion = 2;

    /// <summary>
    /// The keys deeper than its own a table of sketches holds the distinct sketch of in each row
    /// (<see cref="Aggregating.CardinalitySketch.Encode"/>), which an approximate distinct count of one merges; none for
    /// another table, and for one written before them.
    /// </summary>
    internal int[] Distinct { get; init; } = [];

    /// <summary>
    /// The keys of <see cref="Distinct"/> whose sketches miss rows, the merge that fed them having stopped carrying them
    /// past an hour: their columns are written, and no question reads them.
    /// </summary>
    internal int[] Lost { get; init; } = [];

    /// <summary>Whether the table holds the whole distinct sketch of key <paramref name="key"/>.</summary>
    internal bool HoldsDistinct(int key) => Array.IndexOf(Distinct, key) >= 0 && Array.IndexOf(Lost, key) < 0;

    /// <summary>
    /// The mantissa bits of the grid of a table of sketches: 0.77 %, the grid a quantile within 1 %, the default, asks
    /// for. A coarser grid is read off it exactly, its buckets the unions of this one's.
    /// </summary>
    internal const int SketchBits = 6;

    /// <summary>
    /// The entry's options: the kind, the bucket, the keys, the measures, the keys of its distinct sketches and of those
    /// lost when it holds any, and the rows, behind a version byte, the second when it does; then, for a table in chunks,
    /// each chunk's first and last bucket and its rows, in the order of its run's payloads.
    /// </summary>
    internal byte[] OptionBytes(IReadOnlyList<TableChunk>? chunks = null)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Distinct.Length > 0 ? DistinctVersion : Version);
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

            if (Distinct.Length > 0)
            {
                writer.WriteVarint((ulong)Distinct.Length);
                foreach (int key in Distinct)
                {
                    writer.WriteVarint((ulong)key);
                }

                writer.WriteVarint((ulong)Lost.Length);
                foreach (int key in Lost)
                {
                    writer.WriteVarint((ulong)key);
                }
            }

            writer.WriteVarint((ulong)Rows);
            if (chunks is { Count: > 0 })
            {
                writer.WriteVarint((ulong)chunks.Count);
                foreach (TableChunk chunk in chunks)
                {
                    writer.WriteVarint(unchecked((ulong)chunk.FirstBucket));
                    writer.WriteVarint(unchecked((ulong)chunk.LastBucket));
                    writer.WriteVarint((ulong)chunk.Rows);
                }
            }

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
    /// integer, five for a float), its least and its greatest, all 64-bit; for a table of sketches, then each
    /// measure's sketch and each of its <see cref="Distinct"/> keys' distinct sketch, binary. The writer builds its rows at
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

        if (Kind == SynopsisTableKind.Sketches)
        {
            DType sketch = types.Binary(Nullability.NonNullable);
            foreach (int measure in Measures)
            {
                names.Add($"{NameOf(measure, schema, paths)}.sketch");
                fields.Add(sketch);
            }

            foreach (int key in Distinct)
            {
                names.Add($"{NameOf(key, schema, paths)}.distinct");
                fields.Add(sketch);
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
    internal static SynopsisTableInfo? From(ReadOnlySpan<byte> bytes) => From(bytes, out _);

    /// <summary>
    /// The table the options <paramref name="bytes"/> describe, and its chunks' buckets and rows when they say them;
    /// null when they do not decode.
    /// </summary>
    internal static SynopsisTableInfo? From(ReadOnlySpan<byte> bytes, out List<(long First, long Last, int Rows)>? chunks)
    {
        chunks = null;
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            ulong version = reader.ReadVarint();
            if (version is not (Version or DistinctVersion))
            {
                return null;
            }

            ulong kind = reader.ReadVarint();
            ulong bucket = reader.ReadVarint();
            int[]? keys = Fields(ref reader, bytes.Length);
            int[]? measures = keys is null ? null : Fields(ref reader, bytes.Length);
            int[]? distinct = version == DistinctVersion && measures is not null ? Fields(ref reader, bytes.Length) : [];
            int[]? lost = version == DistinctVersion && distinct is not null ? Fields(ref reader, bytes.Length) : [];
            int rows = checked((int)reader.ReadVarint());
            if (measures is not null && !reader.End)
            {
                ulong count = reader.ReadVarint();
                if (count > (ulong)bytes.Length)
                {
                    return null;
                }

                chunks = new List<(long, long, int)>((int)count);
                for (ulong c = 0; c < count; c++)
                {
                    long first = unchecked((long)reader.ReadVarint());
                    long last = unchecked((long)reader.ReadVarint());
                    chunks.Add((first, last, checked((int)reader.ReadVarint())));
                }
            }

            return measures is not null && distinct is not null && lost is not null && reader.End && kind <= (ulong)SynopsisTableKind.Sketches
                && bucket <= (ulong)SynopsisBucket.Object && (distinct.Length == 0 || kind == (ulong)SynopsisTableKind.Sketches)
                && Array.TrueForAll(lost, key => Array.IndexOf(distinct, key) >= 0)
                ? new SynopsisTableInfo((SynopsisTableKind)kind, (SynopsisBucket)bucket, keys!, measures, rows) { Distinct = distinct, Lost = lost }
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
    /// and keys each, and their distributions for a table of sketches, weighed alike. Answering one costs its
    /// source's bytes at <paramref name="prices"/>'s price of a byte, and its rows at the price of a row
    /// grouped: the columns it sums, whose bytes <paramref name="columnBytes"/> gives, over the object's
    /// <paramref name="rows"/>, or the cheapest table kept that answers it by merging its rows. Each round
    /// keeps the table that lowers the questions' costs the most per byte it holds, as long as it lowers them
    /// and fits what is left of <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// A table answers a question when its bucket is as fine or finer, a slice's nesting in its hour and an
    /// hour's in the object, its keys hold the question's, and its measures too, sketched when the question's
    /// are. Of two tables that hold the same rows, the one weighed second saves nothing, and is left out.
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
            asks[c] = questions.FindIndex(q =>
                q.Bucket == table.Bucket && (q.Kind == SynopsisTableKind.Sketches) == (table.Kind == SynopsisTableKind.Sketches)
                && q.Keys.Length == table.Keys.Length && Holds(q.Keys, table.Keys));
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

    /// <summary>
    /// Whether this table's rows give <paramref name="other"/>'s, merged: a bucket as fine or finer, its keys and measures,
    /// and its sketches, which only a table of sketches holds.
    /// </summary>
    private bool Answers(SynopsisTableInfo other) =>
        Bucket <= other.Bucket && (Kind == SynopsisTableKind.Sketches || other.Kind != SynopsisTableKind.Sketches)
        && Holds(Keys, other.Keys) && Holds(Measures, other.Measures);

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
        DTypeArena types = new DTypeArena();
        List<Wanted> found = [];
        foreach (IndexEntry entry in directory?.Entries ?? [])
        {
            // The tables lie in the file itself: an append drops them. A table's chunks are its run's payloads, the
            // buckets and rows of each in the entry's options after the table's own.
            if (entry.Kind == IndexKinds.SynopsisTable && From(entry.Options, out List<(long First, long Last, int Rows)>? chunks) is { } table
                && entry.Runs is [IndexRun { Origin: 0, Payload: { Count: > 0 } payload, PayloadDTypes: { } dtypes }]
                && dtypes.Count == payload.Count)
            {
                for (int c = 0; c < payload.Count; c++)
                {
                    (long first, long last, int rows) = chunks is { } held && held.Count == payload.Count
                        ? held[c]
                        : payload.Count == 1 ? (long.MinValue, long.MaxValue, table.Rows) : (0, -1, -1);
                    if (rows < 0)
                    {
                        break;
                    }

                    // A chunk whose struct does not decode is left out, as one whose rows do not.
                    DType row;
                    try
                    {
                        row = DTypeFlatBuffers.Read(dtypes[c], types);
                    }
                    catch (VortexFormatException)
                    {
                        continue;
                    }

                    found.Add(new Wanted(table, new TableChunk(payload[c], first, last, rows), row));
                }
            }
        }

        if (found.Count == 0)
        {
            return new SynopsisTableSet(new ScanContext([]), []);
        }

        return await DecodeAsync(file.Segments, file.CreateIndexContext(0), [.. found], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The synopsis tables of a file its table of contents <paramref name="contents"/> names, read from
    /// <paramref name="source"/> without an open: the chunks <paramref name="reads"/> keeps, every one when it
    /// is null, their regions in one request set, each chunk's rows decoded at its table's <see cref="RowType"/>
    /// over the file's schema, which the contents hold, and only the fields of them <paramref name="reads"/> names.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static async ValueTask<SynopsisTableSet> ReadAsync(
        IO.ISegmentReader source, SynopsisContents contents, ISynopsisReads? reads, VortexReadOptions? options,
        CancellationToken cancellationToken)
    {
        Wanted[] wanted = WantedOf(contents, reads);
        (ArrayEncodingId[] resolved, string[] ids) = contents.EncodingTable;
        ScanContext context = Contexts.RentDetached(options ?? VortexReadOptions.Default, resolved, ids);
        SynopsisTableSet set = await DecodeAsync(source, context, wanted, cancellationToken, Contexts, reads).ConfigureAwait(false);
        set.Types = contents.TableRowTypes.Arena;
        return set;
    }

    /// <summary>
    /// The tables <see cref="ReadAsync(IO.ISegmentReader, SynopsisContents, ISynopsisReads?, VortexReadOptions?, CancellationToken)"/>
    /// reads, when the synopsis cache holds every chunk wanted: from the disk, with no reader of the object, no wait and no
    /// request of its own; null when one is not there, which a reader then reads.
    /// </summary>
    /// <remarks>
    /// A warm question asks every object this, where each opened a reader of the store, one of the cache and one of the
    /// session over it, a few hundred bytes and three layers of waits an object, for regions the disk held.
    /// </remarks>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static SynopsisTableSet? ReadCached(SynopsisCache cache, UInt128 uid, SynopsisContents contents, ISynopsisReads? reads, VortexReadOptions? options)
    {
        // Every chunk held, asked before one is read: a read that falls back to the reader then counts no hit twice.
        Wanted[] wanted = WantedOf(contents, reads);
        if (wanted.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < wanted.Length; i++)
        {
            if (!cache.Holds(uid, wanted[i].Chunk.Region))
            {
                return null;
            }
        }

        (ArrayEncodingId[] resolved, string[] ids) = contents.EncodingTable;
        ScanContext context = Contexts.RentDetached(options ?? VortexReadOptions.Default, resolved, ids);
        IO.SegmentRequestSet requests = context.Segments;
        bool missed = false;
        bool kept = false;
        try
        {
            if (Add(requests, wanted) is false)
            {
                missed = true;
                return null;
            }

            for (int slot = 0; slot < requests.Count; slot++)
            {
                Serialization.Schemas.SegmentSpec spec = requests.GetSpec(slot);
                IndexSegment region = RegionOf(wanted, spec);
                cache.Asked?.Invoke(uid, region);
                if (!cache.TryRead(uid, region, out Buffers.SegmentOwner owner))
                {
                    missed = true;
                    return null;
                }

                requests.SetResult(slot, owner);
                requests.NoteCacheHit();
            }

            requests.Complete();
            SynopsisTableSet set = Decoded(context, wanted, requests, Contexts, reads, verified: true);
            set.Types = contents.TableRowTypes.Arena;
            kept = true;
            return set;
        }
        catch
        {
            context.Dispose();
            throw;
        }
        finally
        {
            if (!kept)
            {
                requests.Release();
            }

            if (missed)
            {
                Contexts.Return(context);
            }
        }
    }

    /// <summary>
    /// The chunks of <paramref name="contents"/>'s tables <paramref name="reads"/> keeps, every one when it is null, each with
    /// its table's row type: counted, then named, in an array of their number, a few an object, where lists of them grew for
    /// each. By index, as every list of the contents: an interface's enumerator is an allocation a list.
    /// </summary>
    private static Wanted[] WantedOf(SynopsisContents contents, ISynopsisReads? reads)
    {
        int count = 0;
        for (int t = 0; t < contents.Tables.Count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = contents.Tables[t];
            if (reads?.ReadsTable(table) == false)
            {
                continue;
            }

            for (int c = 0; c < chunks.Count; c++)
            {
                count += reads?.Reads(table, chunks[c]) ?? true ? 1 : 0;
            }
        }

        // The tables' row types, built once for the contents and shared by every read of them.
        DType[] rows = contents.TableRowTypes.Rows;
        Wanted[] wanted = count == 0 ? [] : new Wanted[count];
        int at = 0;
        for (int t = 0; t < contents.Tables.Count && at < count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = contents.Tables[t];
            if (reads?.ReadsTable(table) == false)
            {
                continue;
            }

            for (int c = 0; c < chunks.Count; c++)
            {
                if (reads?.Reads(table, chunks[c]) ?? true)
                {
                    wanted[at++] = new Wanted(table, chunks[c], rows[t]);
                }
            }
        }

        return wanted;
    }

    /// <summary>The region of <paramref name="wanted"/>'s chunk <paramref name="spec"/> names: its offset and length.</summary>
    private static IndexSegment RegionOf(Wanted[] wanted, in Serialization.Schemas.SegmentSpec spec)
    {
        for (int i = 0; i < wanted.Length; i++)
        {
            IndexSegment region = wanted[i].Chunk.Region;
            if ((ulong)region.Offset == spec.Offset && (uint)region.Length == spec.Length)
            {
                return region;
            }
        }

        throw new InvalidOperationException("A slot of a read of tables names no chunk of them.");
    }

    /// <summary>Registers each chunk's region in <paramref name="requests"/>, its slot noted; false when <paramref name="wanted"/> is empty.</summary>
    private static bool Add(IO.SegmentRequestSet requests, Wanted[] wanted)
    {
        for (int i = 0; i < wanted.Length; i++)
        {
            IndexSegment region = wanted[i].Chunk.Region;
            wanted[i].Slot = requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
        }

        return wanted.Length > 0;
    }

    /// <summary>
    /// The contexts the tables of objects read without their open are decoded into, kept from one object to the next:
    /// a context is a dozen arrays, which every object of a question built and dropped, half of what a warm question
    /// over 48 objects allocated. Their arenas of dtypes are kept too, the batches of a set holding none of them once
    /// the set is let go: an arena made for each object was a sixth of what such a question allocated.
    /// </summary>
    private static ScanContexts Contexts { get; } = new ScanContexts(Math.Max(16, 3 * Environment.ProcessorCount)) { KeepsTypes = true }.Swept();

    /// <summary>
    /// Reads the chunks of <paramref name="wanted"/> in one request set, the context's own, and decodes each into
    /// <paramref name="context"/>'s arena at its table's row type, only the fields <paramref name="reads"/> names of
    /// it; a chunk whose region does not hold its checksum, or does not decode, is left out.
    /// </summary>
    private static async ValueTask<SynopsisTableSet> DecodeAsync(
        IO.ISegmentReader source, ScanContext context, Wanted[] wanted, CancellationToken cancellationToken, ScanContexts? pool = null,
        ISynopsisReads? reads = null)
    {
        // The context's set, which a context given back holds empty: the rows lie in the bytes read, which the set of
        // tables keeps and lets go with them.
        IO.SegmentRequestSet requests = context.Segments;
        bool kept = false;
        try
        {
            if (!Add(requests, wanted))
            {
                return new SynopsisTableSet(context, [], pool);
            }

            await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            SynopsisTableSet set = Decoded(context, wanted, requests, pool, reads, verified: false);
            kept = true;
            return set;
        }
        catch
        {
            context.Dispose();
            throw;
        }
        finally
        {
            if (!kept)
            {
                requests.Release();
            }
        }
    }

    /// <summary>
    /// The chunks of <paramref name="wanted"/>, read into <paramref name="requests"/>, each decoded into <paramref name="context"/>'s
    /// arena at its table's row type, only the fields <paramref name="reads"/> names of it; a chunk whose region does not hold
    /// its checksum, or does not decode, is left out. The checksums of regions the synopsis cache served, which verified
    /// them, are not reckoned again (<paramref name="verified"/>).
    /// </summary>
    private static SynopsisTableSet Decoded(
        ScanContext context, Wanted[] wanted, IO.SegmentRequestSet requests, ScanContexts? pool, ISynopsisReads? reads, bool verified)
    {
        ReadTable[] tables = new ReadTable[wanted.Length];
        int read = 0;
        for (int i = 0; i < wanted.Length; i++)
        {
            (SynopsisTableInfo table, TableChunk chunk, DType row, int slot) = wanted[i];
            Buffers.VortexBuffer bytes = requests.GetBuffer(slot);
            if (!verified && !chunk.Region.Holds(bytes.Span))
            {
                continue;
            }

            // A table's rows are a struct of one array node: its decoder reads the fields the projection
            // names and no others, a question of a count reading 3 of a table's 16 columns.
            Layouts.FieldMask fields = reads?.Fields(table) ?? Layouts.FieldMask.All;
            ulong encoded = reads?.Encoded(table) ?? 0;
            try
            {
                // The rows decoded where they lie, in the bytes read, which the set keeps: a copy of them into the arena
                // was a tenth of a question that folds a million rows of runs.
                context.Decode.LoadBlob(bytes);
                ArrayNode root = context.Nodes.Root;
                if (!fields.IsAll)
                {
                    context.ExchangePushedFields(fields);
                    context.FieldsHonoured = false;
                }

                // The keys a group by reads by their codes stay a dictionary's, where their values were gathered at
                // every row and then looked up again, a third of a question that folds a million rows of runs.
                if (encoded != 0)
                {
                    context.ExchangeEncodedFields(encoded);
                }

                int node = context.Decode.DecodeRoot(in root, row, chunk.Rows);
                bool narrowed = !fields.IsAll && context.FieldsHonoured;
                tables[read++] = new ReadTable(table, chunk, node) { Fields = narrowed ? fields : Layouts.FieldMask.All };
            }
            catch (VortexFormatException)
            {
            }
            finally
            {
                // A root no struct decoder read leaves the projection pushed, which no other chunk is to take.
                if (!fields.IsAll)
                {
                    context.ExchangePushedFields(Layouts.FieldMask.All);
                }

                if (encoded != 0)
                {
                    context.ExchangeEncodedFields(0);
                }
            }
        }

        return new SynopsisTableSet(context, read == tables.Length ? tables : tables[..read], pool);
    }

    /// <summary>A chunk of a table a read fetches: the table, the chunk, the row type it decodes at, and the slot of its region in the read.</summary>
    private record struct Wanted(SynopsisTableInfo Table, TableChunk Chunk, DType Row)
    {
        internal int Slot { get; set; }

        internal readonly void Deconstruct(out SynopsisTableInfo table, out TableChunk chunk, out DType row, out int slot) =>
            (table, chunk, row, slot) = (Table, Chunk, Row, Slot);
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
    private readonly ScanContexts? _pool;
    private int _disposed;

    /// <param name="context">The context the rows were decoded into.</param>
    /// <param name="tables">Each chunk read of a table, and its rows.</param>
    /// <param name="pool">The pool the context goes back to once the set is let go; null to dispose it.</param>
    internal SynopsisTableSet(ScanContext context, IReadOnlyList<ReadTable> tables, ScanContexts? pool = null)
    {
        _context = context;
        _pool = pool;
        Tables = tables;
    }

    /// <summary>Each chunk read of a table, and its rows, a table's chunks in the order of their buckets.</summary>
    internal IReadOnlyList<ReadTable> Tables { get; }

    /// <summary>The arena the rows lie in.</summary>
    internal CanonicalArena Arena => _context.Canonical;

    /// <summary>The arena of the rows' dtypes, where a struct over their columns is built; null when the reader does not say.</summary>
    internal DTypeArena? Types { get; set; }

    /// <summary>Lets the rows go, once, and the bytes they lie in: a context given back twice would be lent to two readers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _context.Segments.Release();
        if (_pool is { } pool)
        {
            pool.Return(_context);
        }
        else
        {
            _context.Dispose();
        }
    }
}

/// <summary>A chunk of a table as read: the table, the chunk, and its rows, a struct node of the set's arena.</summary>
/// <param name="Table">The table.</param>
/// <param name="Chunk">The chunk read of it.</param>
/// <param name="Node">Its rows.</param>
internal readonly record struct ReadTable(SynopsisTableInfo Table, TableChunk Chunk, int Node)
{
    /// <summary>The fields of the table's row its struct holds, in their order: every one, or those a projection named.</summary>
    internal Layouts.FieldMask Fields { get; init; }
}

/// <summary>What a read of an object's synopsis tables fetches: the chunks it decodes, and the fields of each table's rows it decodes.</summary>
internal interface ISynopsisReads
{
    /// <summary>Whether <paramref name="chunk"/> of <paramref name="table"/> is read.</summary>
    bool Reads(SynopsisTableInfo table, TableChunk chunk);

    /// <summary>Whether a chunk of <paramref name="table"/> may be read: the chunks of a table it is not are not looked at, nor parsed.</summary>
    bool ReadsTable(SynopsisTableInfo table);

    /// <summary>The fields of <paramref name="table"/>'s rows decoded: <see cref="Layouts.FieldMask.All"/> for every one.</summary>
    Layouts.FieldMask Fields(SynopsisTableInfo table);

    /// <summary>
    /// The fields of <paramref name="table"/>'s rows, a bit each below 64, whose columns may stay a dictionary or run-end
    /// node, which the reader reads encoded (<see cref="Arrays.ScanContext.EncodedFields"/>); 0 for every column canonical.
    /// </summary>
    ulong Encoded(SynopsisTableInfo table);
}
