using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
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
    /// measure's sketch and each of its <see cref="Distinct"/> keys' distinct sketch, binary. The names are those a batch
    /// of states reads (<see cref="StateRequest"/>), a key's the one <see cref="StateRequest.KeyColumn"/> gives it, so
    /// that no key takes another column's. The writer builds its rows at this type, and a reader decodes them at it.
    /// </summary>
    internal DType RowType(DType schema, IReadOnlyList<SynopsisPath> paths, DTypeArena types)
    {
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        List<string> names = [StateRequest.BucketColumn];
        List<DType> fields = [longs];
        foreach (int key in Keys)
        {
            names.Add(StateRequest.KeyColumn(NameOf(key, schema, paths)));
            fields.Add(SynopsisPath.Names(key)
                ? ClassType(paths[SynopsisPath.Place(key)].Class, types)
                : VortexTypes.ToDType(VortexTypes.FromDType(schema.GetField(key)), types));
        }

        names.Add(StateRequest.RowsColumn);
        fields.Add(longs);
        foreach (int measure in Measures)
        {
            foreach (string name in MeasureLayout.Names(NameOf(measure, schema, paths), SumsFloats(measure, schema, paths)))
            {
                names.Add(name);
                fields.Add(longs);
            }
        }

        if (Kind == SynopsisTableKind.Sketches)
        {
            DType sketch = types.Binary(Nullability.NonNullable);
            foreach (int measure in Measures)
            {
                names.Add(StateRequest.SketchOf(NameOf(measure, schema, paths)));
                fields.Add(sketch);
            }

            foreach (int key in Distinct)
            {
                names.Add(StateRequest.DistinctOf(NameOf(key, schema, paths)));
                fields.Add(sketch);
            }
        }

        return types.Struct([.. names], [.. fields], Nullability.NonNullable);
    }

    /// <summary>
    /// The two structs a chunk of a table of sketches lays its rows in when it lays its <paramref name="distinct"/> distinct
    /// sketches apart, built in <paramref name="types"/>: the first fields of <paramref name="row"/>, in one region, and its
    /// last, the distinct sketches, in another, which a question that counts no distinct value does not read.
    /// </summary>
    internal static (DType Main, DType Distinct) RowHalves(DType row, int distinct, DTypeArena types)
    {
        int width = row.FieldCount;
        string[] names = new string[width];
        DType[] fields = new DType[width];
        for (int f = 0; f < width; f++)
        {
            names[f] = row.GetFieldName(f);
            fields[f] = row.GetField(f);
        }

        int first = width - distinct;
        return (types.Struct(names.AsSpan(0, first), fields.AsSpan(0, first), row.Nullability),
            types.Struct(names.AsSpan(first), fields.AsSpan(first), row.Nullability));
    }

    /// <summary>
    /// The name of a key or measure <paramref name="entry"/>: its field's, or its path's, <c>field["key"]</c>. A measure's
    /// states are named from it, and a key's column in a table by <see cref="StateRequest.KeyColumn"/>.
    /// </summary>
    internal static string NameOf(int entry, DType schema, IReadOnlyList<SynopsisPath> paths) =>
        SynopsisPath.Names(entry) && paths[SynopsisPath.Place(entry)] is { } path
            ? $"{schema.GetFieldName(path.Field)}[\"{path.Key}\"]"
            : schema.GetFieldName(entry);

    /// <summary>
    /// Whether the sum of measure <paramref name="measure"/> takes a float's five words rather than an integer's two: a
    /// path of doubles, or a column whose storage holds floats.
    /// </summary>
    internal static bool SumsFloats(int measure, DType schema, IReadOnlyList<SynopsisPath> paths)
    {
        if (SynopsisPath.Names(measure))
        {
            return paths[SynopsisPath.Place(measure)].Class == Types.Variants.VariantClass.Double;
        }

        DType dtype = schema.GetField(measure);
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype.PType.IsFloat();
    }

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
}
