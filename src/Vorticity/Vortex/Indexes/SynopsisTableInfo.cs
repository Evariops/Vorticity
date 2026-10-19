using System;
using System.Collections.Generic;
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
    /// Which of <paramref name="tables"/> are worth their bytes: a table is kept when it holds fewer bytes
    /// than what answers its questions without it, the smallest of the tables kept that do, by merging
    /// their rows, or else the columns it summarizes, whose bytes <paramref name="columnBytes"/> gives.
    /// </summary>
    /// <remarks>
    /// A table answers another's questions when its bucket is as fine or finer, a slice's nesting in its
    /// hour and an hour's in the object, its keys hold the other's, and its measures too. Tables are
    /// weighed finest first, then deepest first, so that the tables that could answer one are weighed
    /// before it, and of two tables that hold the same rows, the second is never kept. Bytes alone weigh
    /// here: the costs of walking a table's rows and of grouping a data row come with the choice under
    /// the synopsis's budget.
    /// </remarks>
    internal static bool[] WorthTheirBytes(IReadOnlyList<(SynopsisTableInfo Table, long Bytes)> tables, Func<int, long> columnBytes)
    {
        int[] order = new int[tables.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (left, right) =>
        {
            SynopsisTableInfo l = tables[left].Table;
            SynopsisTableInfo r = tables[right].Table;
            int compared = l.Bucket.CompareTo(r.Bucket);
            compared = compared != 0 ? compared : r.Keys.Length.CompareTo(l.Keys.Length);
            compared = compared != 0 ? compared : l.Kind.CompareTo(r.Kind);
            return compared != 0 ? compared : left.CompareTo(right);
        });

        bool[] kept = new bool[tables.Count];
        foreach (int i in order)
        {
            (SynopsisTableInfo table, long bytes) = tables[i];
            long without = 0;
            foreach (int field in table.Keys)
            {
                without += columnBytes(field);
            }

            foreach (int field in table.Measures)
            {
                without += columnBytes(field);
            }

            for (int j = 0; j < kept.Length; j++)
            {
                if (kept[j] && tables[j].Table.Answers(table))
                {
                    without = Math.Min(without, tables[j].Bytes);
                }
            }

            kept[i] = bytes < without;
        }

        return kept;
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

        ScanContext context = file.CreateIndexContext(0);
        try
        {
            using IO.SegmentRequestSet requests = new IO.SegmentRequestSet(found.Count);
            int[] slots = new int[found.Count];
            for (int i = 0; i < found.Count; i++)
            {
                IndexSegment region = found[i].Region;
                slots[i] = requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            List<(SynopsisTableInfo, int)> tables = [];
            DTypeArena types = new DTypeArena();
            for (int i = 0; i < found.Count; i++)
            {
                (SynopsisTableInfo table, IndexSegment region, byte[] dtype) = found[i];
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
                    int node = context.Decode.DecodeRoot(in root, DTypeFlatBuffers.Read(dtype, types), table.Rows);
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

    public void Dispose() => _context.Dispose();
}
