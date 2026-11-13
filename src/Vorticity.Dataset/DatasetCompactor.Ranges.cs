using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Writing;

namespace Vorticity.Dataset;

internal static partial class DatasetCompactor
{
    /// <summary>The most ranges of the key a consolidation runs side by side.</summary>
    private const int MostRanges = 8;

    /// <summary>
    /// What an open input holds besides its batch, at least: its reader, its footer parsed, the segments of the chunks it
    /// reads. Measured at 0.10 to 0.15 MB an input, linear from 8 to 3 283 open inputs of 24 KB (2026-11-13).
    /// </summary>
    internal const long InputFloorBytes = 128 << 10;

    /// <summary>
    /// What an input holds for each byte of the batch it hands the merge, about: the batch, the chunks it was cut from,
    /// decoded whole, and what its columns' reshaping keeps. Measured at 3.3 on a metric's records: 1.78 KB of resident
    /// memory a row for 530 bytes of batch, over merges of 8 inputs of 13 000 to 53 000 rows (2026-11-13).
    /// </summary>
    private const long HeldPerBatchByte = 3;

    /// <summary>
    /// What a range of a consolidation holds, about: <see cref="InputsBytes"/> of the range of the most inputs, in batches
    /// of <paramref name="batchRows"/>, and its objects' <paramref name="writer"/> (<see cref="WriterBytes"/>).
    /// </summary>
    private static long RangeBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, KeyRanges ranges, long writer, int batchRows) =>
        InputsBytes(dataset, schema, job, ranges.MostInputs, batchRows) + writer;

    /// <summary>
    /// What a decoded row of the job's inputs weighs, about: what the handle's last merge measured, or, before any, the
    /// least its schema's columns take decoded (<see cref="FloorBytes"/>). The bytes a row is written in mislead on small
    /// objects, whose chunks' fixed costs make a row of four 51 KB.
    /// </summary>
    internal static long RowBytes(VortexDataset dataset, CompactionJob job) =>
        dataset.DecodedRowBytes > 0 ? dataset.DecodedRowBytes : FloorBytes(dataset.Snapshot.Schema.DType);

    /// <summary>
    /// The least a row of <paramref name="dtype"/> takes decoded: a number its width, a text or a binary its view, a list its
    /// offset and size and one element, a variant a view. What its values hold past that, the bytes of long texts, the
    /// elements of long lists, a merge measures.
    /// </summary>
    internal static long FloorBytes(Types.DType dtype)
    {
        switch (dtype.Kind)
        {
            case Types.DTypeKind.Primitive:
                return Types.PTypeExtensions.ByteWidth(dtype.PType);
            case Types.DTypeKind.Bool:
            case Types.DTypeKind.Null:
                return 1;
            case Types.DTypeKind.Struct:
                long sum = 0;
                for (int field = 0; field < dtype.FieldCount; field++)
                {
                    sum += FloorBytes(dtype.GetField(field));
                }

                return Math.Max(sum, 1);
            case Types.DTypeKind.List:
                return 8 + FloorBytes(dtype.ElementType);
            case Types.DTypeKind.FixedSizeList:
                return Math.Max(1, (long)dtype.FixedSize * FloorBytes(dtype.ElementType));
            case Types.DTypeKind.Extension:
                return FloorBytes(dtype.StorageType);
            default:
                return 16;
        }
    }

    /// <summary>
    /// Learns what a row of the job's inputs weighs decoded, when the handle has not yet: the first batch of its input of
    /// the most rows, 8 192 rows at most, read and measured once, before the job sizes its batches by it.
    /// </summary>
    private static async ValueTask LearnRowBytesAsync(VortexDataset dataset, DatasetSchema schema, CompactionJob job, CancellationToken cancellationToken)
    {
        if (dataset.DecodedRowBytes > 0)
        {
            return;
        }

        CompactionInput largest = job.Inputs[0];
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Entry.PhysicalRows > largest.Entry.PhysicalRows)
            {
                largest = input;
            }
        }

        RowMeter meter = new RowMeter();
        ObjectLease lease = await dataset.RentAsync(largest.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in RowsOfAsync(lease, largest.Entry, schema, cancellationToken, batchRows: 8_192).ConfigureAwait(false))
            {
                meter.Add(batch);
                break;
            }
        }

        dataset.LearnDecodedRowBytes(meter.BytesPerRow);
    }

    /// <summary>
    /// What an open input of the job holds besides its batch, about: <see cref="HeldPerOpenByte"/> times the chunks it holds
    /// open (<see cref="OpenChunkBytes(VortexDataset, DatasetSchema, CompactionJob)"/>), and <see cref="InputFloorBytes"/> at least.
    /// </summary>
    internal static long OpenBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job) =>
        Math.Max(InputFloorBytes, HeldPerOpenByte * OpenChunkBytes(dataset, schema, job));

    /// <summary>
    /// The rows of an input's batch in a merge of <paramref name="inputs"/> of the job's inputs, rows of
    /// <paramref name="rowBytes"/> decoded, whose batches may hold <paramref name="share"/> bytes together: the rows
    /// <see cref="MergeRows"/> gives each, fewer where the share holds fewer, a power of two between a block of 8 192 rows
    /// and a window (<see cref="FlatLayoutReader.WindowRows"/>). Rows of a metric's maps and lists weigh four times a
    /// log's: by rows alone, eight inputs of a window each held 1.7 GB of batches.
    /// </summary>
    internal static int InputBatchRows(int inputs, long rowBytes, long share)
    {
        long byCount = MergeRows / Math.Max(1, inputs);
        long byBytes = share / ((long)Math.Max(1, inputs) * Math.Max(rowBytes, 1));
        long rows = Math.Max(Math.Min(byCount, byBytes), 1);
        return Math.Clamp(1 << BitOperations.Log2((ulong)Math.Min(rows, int.MaxValue)), 8_192, FlatLayoutReader.WindowRows);
    }

    /// <summary>
    /// The rows of an input's batch in a merge of <paramref name="inputs"/> of the job's inputs under a budget of
    /// <paramref name="budget"/> bytes, of which its writer holds <paramref name="writer"/>: what is left once the inputs'
    /// open bytes are taken goes to their batches (<see cref="InputBatchRows(int, long, long)"/>).
    /// </summary>
    internal static int InputBatchRows(VortexDataset dataset, DatasetSchema schema, CompactionJob job, int inputs, long budget, long writer) =>
        InputBatchRows(inputs, HeldPerBatchByte * RowBytes(dataset, job), budget - writer - ((long)inputs * OpenBytes(dataset, schema, job)));

    /// <summary>
    /// What the job holds at least, its inputs' batches at a block of 8 192 rows: its level 0 inputs, which overlap and are
    /// all open at once, one input of the levels above, which are disjoint and open one after another, and its writer.
    /// </summary>
    internal static long LeastBytes(VortexDataset dataset, CompactionJob job)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long held = 0;
        long above = 0;
        int open = 1;
        foreach (CompactionInput input in job.Inputs)
        {
            long rows = Math.Min(input.Entry.PhysicalRows, 8_192);
            if (input.Level == 0)
            {
                held += rows;
                open++;
            }
            else
            {
                above = Math.Max(above, rows);
            }
        }

        return ((held + above) * HeldPerBatchByte * RowBytes(dataset, job))
            + ((long)open * OpenBytes(dataset, schema, job))
            + WriterBytes(dataset, schema, job, null);
    }

    /// <summary>Whether the job holds within the jobs' budget at its least (<see cref="LeastBytes"/>).</summary>
    internal static bool Fits(VortexDataset dataset, CompactionJob job) => LeastBytes(dataset, job) <= JobBudget(dataset);

    /// <summary>
    /// The rows of an input's batch in the first pass of a job, which counts its inputs one a lane: what a lane's share of the
    /// budget holds of them.
    /// </summary>
    private static int CensusRows(VortexDataset dataset, CompactionJob job) =>
        InputBatchRows(1, HeldPerBatchByte * RowBytes(dataset, job), JobBudget(dataset) / Math.Max(1, dataset.Session.Options.MaxDegreeOfParallelism));

    /// <summary>
    /// The bytes of the jobs' memory budget of the handle's session: what one job may plan to hold.
    /// </summary>
    internal static long JobBudget(VortexDataset dataset) =>
        (dataset.Session.Options.JobMemoryBudget ?? QueryMemoryBudget.Jobs).CeilingBytes;

    /// <summary>
    /// What the writer of a job's objects holds, about: each column what it gathers before it writes a chunk, at most
    /// <see cref="VortexFileWriter.MostGatheredPerTarget"/> times the dataset's chunk bytes; a variant column shredded,
    /// <see cref="VariantShredder.HeldPerTarget"/> times its chunk's target. Of the partition values of a job in one
    /// piece, whose objects are written one after the other, the one whose writer holds the most. No more, though, than
    /// the job's rows decoded (<see cref="RowBytes"/>), which the writer holds once at most: a job of 200 000 rows of
    /// metrics was priced 2.6 GB for chunks it never filled, and held 380 MB in all.
    /// </summary>
    private static long WriterBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, IReadOnlyList<ValueShredding>? shreddings) =>
        Math.Min(TargetsBytes(dataset, schema, shreddings), job.Rows * RowBytes(dataset, job));

    /// <summary>What the writer of a job's objects holds once each column gathers its chunk's target (<see cref="WriterBytes"/>).</summary>
    private static long TargetsBytes(VortexDataset dataset, DatasetSchema schema, IReadOnlyList<ValueShredding>? shreddings)
    {
        long most = 0;
        int values = shreddings?.Count ?? 0;
        for (int value = 0; value < Math.Max(values, 1); value++)
        {
            ImmutableDictionary<string, int> chunks = values == 0 ? ImmutableDictionary<string, int>.Empty : shreddings![value].VariantChunks;
            long bytes = (long)(schema.DType.FieldCount - chunks.Count) * VortexFileWriter.MostGatheredPerTarget * dataset.Options.ChunkBytes;
            foreach (KeyValuePair<string, int> chunk in chunks)
            {
                bytes += (long)VariantShredder.HeldPerTarget * chunk.Value;
            }

            most = Math.Max(most, bytes);
        }

        return most;
    }

    /// <summary>
    /// What an input holds of its object besides its batch, in the encoded bytes of the chunks it holds open, about: the
    /// segments it reads, a chunk of each column; a dictionary's values decompressed whole; the children a chunk's windows
    /// share, decoded once for all of them. On S1's logs, inputs of 260 MB held about 30 MB each for 8 MB of segments.
    /// </summary>
    private const long HeldPerOpenByte = 4;

    /// <summary>
    /// What a merge of <paramref name="inputs"/> of the job's inputs holds of them, about: each its open bytes
    /// (<see cref="OpenBytes"/>), and its batch, of <paramref name="batchRows"/> rows or of its own rows when it holds fewer,
    /// at <see cref="RowBytes"/> a row. The inputs of the most rows are counted, which a merge of the job's inputs that
    /// overlap holds open at once: a small input costs what it holds, where a batch of 8 192 rows each priced 3 305 inputs of
    /// four rows at 400 GB.
    /// </summary>
    private static long InputsBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, int inputs, int batchRows)
    {
        int count = Math.Min(inputs, job.Inputs.Count);
        long held = 0;
        if (count == job.Inputs.Count)
        {
            foreach (CompactionInput input in job.Inputs)
            {
                held += Math.Min(input.Entry.PhysicalRows, batchRows);
            }
        }
        else
        {
            long[] rows = new long[job.Inputs.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = Math.Min(job.Inputs[i].Entry.PhysicalRows, batchRows);
            }

            Array.Sort(rows);
            for (int i = rows.Length - count; i < rows.Length; i++)
            {
                held += rows[i];
            }
        }

        return (held * HeldPerBatchByte * RowBytes(dataset, job)) + ((long)count * OpenBytes(dataset, schema, job));
    }

    /// <summary>
    /// The encoded bytes of the chunks an input of the job holds open, about, averaged over its inputs: a chunk of each
    /// column, its bytes over its chunks as the object's contents count them; for an object whose contents do not say, a
    /// chunk of the dataset's chunk bytes a column, its whole bytes at most.
    /// </summary>
    internal static long OpenChunkBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job)
    {
        if (job.Inputs.Count == 0)
        {
            return 0;
        }

        long sum = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            sum += OpenChunkBytes(dataset, schema, input.Entry);
        }

        return sum / job.Inputs.Count;
    }

    /// <summary>The encoded bytes of the chunks the input of <paramref name="entry"/> holds open (<see cref="OpenChunkBytes(VortexDataset, DatasetSchema, CompactionJob)"/>).</summary>
    private static long OpenChunkBytes(VortexDataset dataset, DatasetSchema schema, ObjectEntry entry)
    {
        long sum = 0;
        if (entry.Contents is { ColumnBytes.Count: > 0 } contents)
        {
            for (int column = 0; column < contents.ColumnBytes.Count; column++)
            {
                long chunks = column < contents.ColumnChunks.Count ? Math.Max(contents.ColumnChunks[column], 1) : 1;
                sum += contents.ColumnBytes[column] / chunks;
            }

            return sum;
        }

        return Math.Min(entry.Bytes, (long)schema.DType.FieldCount * Math.Max(dataset.Options.ChunkBytes, 1));
    }

    /// <summary>
    /// How many of <paramref name="inputs"/>, objects of one size class of one partition in the order of the tree, a tier
    /// merge takes: <paramref name="least"/> whatever the bounds, then as many more as keep their bytes within
    /// <paramref name="bytes"/>, the layout's object size or a caller's bound, and what the merge holds within the jobs'
    /// budget, each input open at once at a block of its rows, the writer at the rows they hold. The whole class when it
    /// fits: one job where a merge of the fan-out at a time took 469 of them to empty a class of 3 283 small objects.
    /// </summary>
    internal static int TierWidth(VortexDataset dataset, IReadOnlyList<CompactionInput> inputs, int least, long bytes)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long budget = JobBudget(dataset);
        long rowBytes = dataset.DecodedRowBytes > 0 ? dataset.DecodedRowBytes : FloorBytes(schema.DType);
        long targets = TargetsBytes(dataset, schema, null);
        long held = 0;
        long rows = 0;
        long sum = 0;
        int taken = 0;
        foreach (CompactionInput input in inputs)
        {
            ObjectEntry entry = input.Entry;
            long cost = (Math.Min(entry.PhysicalRows, 8_192) * HeldPerBatchByte * rowBytes)
                + Math.Max(InputFloorBytes, HeldPerOpenByte * OpenChunkBytes(dataset, schema, entry));
            long writer = Math.Min(targets, (rows + entry.Rows) * rowBytes);
            if (taken >= least && (sum + entry.Bytes > bytes || held + cost + writer > budget))
            {
                break;
            }

            held += cost;
            rows += entry.Rows;
            sum += entry.Bytes;
            taken++;
        }

        return taken;
    }

    /// <summary>
    /// The ranges of the key a consolidation runs side by side: the cuts between them, keys of the tree's order that lead
    /// keys rather than hold whole ones, and the rows of each input each range reads.
    /// </summary>
    private sealed class KeyRanges(byte[][] cuts, RowRange[][] rows)
    {
        /// <summary>The ranges.</summary>
        internal int Count => cuts.Length + 1;

        /// <summary>The key range <paramref name="range"/> starts at, empty for the first.</summary>
        internal ReadOnlyMemory<byte> Low(int range) => range == 0 ? default : cuts[range - 1];

        /// <summary>The key range <paramref name="range"/> ends before, empty for the last.</summary>
        internal ReadOnlyMemory<byte> High(int range) => range == cuts.Length ? default : cuts[range];

        /// <summary>The rows of the job's input <paramref name="input"/> range <paramref name="range"/> reads, empty for none.</summary>
        internal RowRange RowsOf(int input, int range) => rows[input][range];

        /// <summary>The most inputs a range reads.</summary>
        internal int MostInputs { get; } = MostOf(rows, cuts.Length + 1);

        private static int MostOf(RowRange[][] rows, int ranges)
        {
            int most = 0;
            for (int range = 0; range < ranges; range++)
            {
                int inputs = 0;
                foreach (RowRange[] input in rows)
                {
                    inputs += input[range].IsEmpty ? 0 : 1;
                }

                most = Math.Max(most, inputs);
            }

            return most;
        }
    }

    /// <summary>
    /// The ranges of the key a consolidation cuts its rows into, of equal weight, as many as the session's lanes and its bytes
    /// in objects of its target let, at most <see cref="MostRanges"/>; null for one. The cuts are first keys of the inputs'
    /// granules, which their sparse indexes give, a granule weighing its object's bytes over its granules: the rows of a key,
    /// and of an indexed prefix, all fall in one range, which reads of each input the granules that may hold its keys alone.
    /// Null too when the inputs keep no index, or hold more than one partition.
    /// </summary>
    private static async ValueTask<KeyRanges?> KeyRangesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, CancellationToken cancellationToken)
    {
        int most = (int)Math.Min(
            Math.Min(MostRanges, dataset.Session.Options.MaxDegreeOfParallelism), job.Bytes / Math.Max(job.TargetBytes, 1));
        if (most < 2)
        {
            return null;
        }

        // The partition leads every key, before the indexed prefix the indexes hold: the cuts are led by it.
        byte[] partition = schema.Partitioned ? PartitionOf(schema, job.Inputs[0]) : [];
        GranuleKeys?[] indexes = new GranuleKeys?[job.Inputs.Count];
        try
        {
            int granules = 0;
            for (int i = 0; i < indexes.Length; i++)
            {
                CompactionInput input = job.Inputs[i];
                if (schema.Partitioned && !VortexDataset.OrderOf(input.Key).Span.StartsWith(partition))
                {
                    return null;
                }

                if (input.Entry.Contents is { KeyIndex: not null } contents
                    && await dataset.KeyIndexAsync(input.Entry, contents, cancellationToken).ConfigureAwait(false) is { } keys)
                {
                    indexes[i] = keys;
                    granules += keys.Granules;
                }
            }

            int[] widths = schema.Tree is { } tree ? tree.Widths[(schema.Partitioned ? 1 : 0)..].ToArray() : [];
            return granules > 0 ? Cut(job, indexes, granules, most, partition, widths) : null;
        }
        catch (VortexFormatException)
        {
            // A page of an input's index that does not hold the keys its table names: the job runs as one range.
            return null;
        }
        finally
        {
            foreach (GranuleKeys? keys in indexes)
            {
                keys?.Dispose();
            }
        }
    }

    /// <summary>The partition <paramref name="input"/>'s rows hold, row-encoded: what leads its tree key.</summary>
    private static byte[] PartitionOf(DatasetSchema schema, CompactionInput input)
    {
        ReadOnlySpan<byte> key = VortexDataset.OrderOf(input.Key).Span;
        return key[..schema.Tree!.PrefixLength(key, 1)].ToArray();
    }

    /// <summary>
    /// Up to <paramref name="most"/> ranges of equal weight over the granules of <paramref name="indexes"/>, and the rows of
    /// each input each reads (<see cref="KeyRangesAsync"/>); null when the granules hold too few keys to cut. The cuts are
    /// row-encoded, as a tree key is; an index that codes its keys otherwise is read and searched in its own coding, by the
    /// encoded <paramref name="widths"/> of the components past the partition.
    /// </summary>
    private static KeyRanges? Cut(CompactionJob job, GranuleKeys?[] indexes, int granules, int most, byte[] partition, int[] widths)
    {
        // Every granule, an input's place and the granule's packed in a long, sorted by key.
        long[] order = ArrayPool<long>.Shared.Rent(granules);

        // Each index's keys decoded once for the sort, which compares them in any order, row-encoded whatever their coding.
        GranuleKeys.DecodedKeys?[] decoded = new GranuleKeys.DecodedKeys?[indexes.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            decoded[i] = indexes[i] is { } keys ? RowsOf(keys, Widths(widths, keys)) : null;
        }

        List<byte[]> cuts = new List<byte[]>(most - 1);
        try
        {
            int at = 0;
            long total = 0;
            for (int i = 0; i < indexes.Length; i++)
            {
                if (indexes[i] is not { } keys)
                {
                    continue;
                }

                for (int granule = 0; granule < keys.Granules; granule++)
                {
                    order[at++] = ((long)i << 32) | (uint)granule;
                }

                total += WeightOf(job, indexes, i) * keys.Granules;
            }

            Array.Sort(order, 0, granules, new ByGranuleKey(decoded));

            // A cut goes before the first granule whose key follows the one before, once the granules before weigh another
            // share of the whole: shares passed at once over a run of one key make one cut, and one range fewer.
            int shares = 0;
            long before = 0;
            for (int k = 0; k < granules && shares < most - 1; k++)
            {
                int input = (int)(order[k] >> 32);
                ReadOnlySpan<byte> key = decoded[input]![(int)order[k]];
                if (k > 0 && total > 0)
                {
                    int reached = (int)Math.Min(most - 1, before * most / total);
                    if (reached > shares && key.SequenceCompareTo(decoded[(int)(order[k - 1] >> 32)]![(int)order[k - 1]]) > 0)
                    {
                        byte[] cut = new byte[partition.Length + key.Length];
                        partition.CopyTo(cut, 0);
                        key.CopyTo(cut.AsSpan(partition.Length));
                        cuts.Add(cut);
                        shares = reached;
                    }
                }

                before += WeightOf(job, indexes, input);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(order);
        }

        if (cuts.Count == 0)
        {
            return null;
        }

        RowRange[][] rows = new RowRange[indexes.Length][];
        for (int i = 0; i < indexes.Length; i++)
        {
            CompactionInput input = job.Inputs[i];
            RowRange[] read = rows[i] = new RowRange[cuts.Count + 1];
            if (indexes[i] is not { } keys)
            {
                // An input that keeps no index, an imported one, is read whole by each range its keys meet.
                ReadOnlySpan<byte> first = VortexDataset.OrderOf(input.Key).Span;
                ReadOnlySpan<byte> last = input.Entry.LastKey.Span;
                for (int range = 0; range < read.Length; range++)
                {
                    bool meets = (range == cuts.Count || first.SequenceCompareTo(cuts[range]) < 0)
                        && (range == 0 || last.IsEmpty || last.SequenceCompareTo(cuts[range - 1]) >= 0);
                    read[range] = meets ? new RowRange(0, input.Entry.PhysicalRows) : RowRange.Empty;
                }

                continue;
            }

            // The cuts in the index's coding, past the partition its keys leave out.
            byte[][] coded = new byte[cuts.Count][];
            for (int cut = 0; cut < coded.Length; cut++)
            {
                ReadOnlySpan<byte> past = cuts[cut].AsSpan(partition.Length);
                coded[cut] = keys.Coding == KeyCoding.Rows ? past.ToArray() : UnpaddedKeys.Narrow(past, Widths(widths, keys));
            }

            for (int range = 0; range < read.Length; range++)
            {
                long start = range == 0 ? 0 : StartFrom(keys, coded[range - 1]);
                long end = range == cuts.Count ? keys.Rows : EndBefore(keys, coded[range]);
                read[range] = end > start ? new RowRange(start, end) : RowRange.Empty;
            }
        }

        return new KeyRanges([.. cuts], rows);
    }

    /// <summary>The encoded widths of the components <paramref name="keys"/> holds, of <paramref name="widths"/>, those past the partition.</summary>
    private static ReadOnlySpan<int> Widths(int[] widths, GranuleKeys keys) => widths.AsSpan(0, Math.Min(widths.Length, keys.Components));

    /// <summary>
    /// Each granule's key of <paramref name="keys"/>, decoded as the row encoding has it: an index whose texts are unpadded
    /// has them widened, so that the keys of objects of either coding compare.
    /// </summary>
    private static GranuleKeys.DecodedKeys RowsOf(GranuleKeys keys, ReadOnlySpan<int> widths)
    {
        GranuleKeys.DecodedKeys decoded = keys.DecodeAll();
        if (keys.Coding == KeyCoding.Rows)
        {
            return decoded;
        }

        int[] ends = new int[keys.Granules];
        long total = 0;
        for (int granule = 0; granule < ends.Length; granule++)
        {
            total += UnpaddedKeys.WidenedLength(decoded[granule], widths);
            ends[granule] = checked((int)total);
        }

        byte[] rows = new byte[total];
        for (int granule = 0; granule < ends.Length; granule++)
        {
            UnpaddedKeys.Widen(decoded[granule], widths, rows.AsSpan(granule == 0 ? 0 : ends[granule - 1]));
        }

        return new GranuleKeys.DecodedKeys(rows, ends);
    }

    /// <summary>What a granule of the job's input <paramref name="input"/> weighs: its bytes over its granules.</summary>
    private static long WeightOf(CompactionJob job, GranuleKeys?[] indexes, int input) =>
        Math.Max(job.Inputs[input].Entry.Bytes / indexes[input]!.Granules, 1);

    /// <summary>
    /// The first row of an object that may hold a key at or past <paramref name="cut"/>, by its index: the granule before the
    /// first whose key reaches it runs to that key, and may hold some; its rows when its last key lies below.
    /// </summary>
    private static long StartFrom(GranuleKeys keys, ReadOnlySpan<byte> cut)
    {
        int granule = keys.FirstAtOrPast(cut);
        return granule == keys.Granules && keys.LastKey.SequenceCompareTo(cut) < 0
            ? keys.Rows
            : (long)Math.Max(granule - 1, 0) * keys.GranuleRows;
    }

    /// <summary>The row past the last of an object that may hold a key below <paramref name="cut"/>: the first of the first granule whose key reaches it.</summary>
    private static long EndBefore(GranuleKeys keys, ReadOnlySpan<byte> cut) =>
        Math.Min((long)keys.FirstAtOrPast(cut) * keys.GranuleRows, keys.Rows);

    /// <summary>Granules of several indexes, an index's place and a granule's packed in a long, by key, then by place.</summary>
    private sealed class ByGranuleKey(GranuleKeys.DecodedKeys?[] indexes) : IComparer<long>
    {
        public int Compare(long left, long right)
        {
            int order = indexes[(int)(left >> 32)]![(int)left].SequenceCompareTo(indexes[(int)(right >> 32)]![(int)right]);
            return order != 0 ? order : left.CompareTo(right);
        }
    }

    /// <summary>
    /// The ranges of a consolidation merged side by side, each into objects of its own, as many at once as the session's
    /// memory holds their open inputs and their writers, the first unreserved: each range's objects, in the ranges'
    /// order, and the rows they hold together. A range whose object runs past its bound starts again under a bound twice
    /// as large, alone; a failure stops the others, whose objects are given up as a failed job's are.
    /// </summary>
    private static async ValueTask<(ObjectStream[] Streams, long Rows, long Reserved, int AtOnce)> RangesAsync(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        KeyRanges ranges,
        long writer,
        RowMeter meter,
        Func<int, ObjectStream> outputsOf,
        CancellationToken cancellationToken)
    {
        ObjectStream[] streams = new ObjectStream[ranges.Count];
        long[] rows = new long[ranges.Count];

        // Each range reserves what it holds before it starts, the first whether the budget grants it or not, as a job in one
        // piece runs; the others wait for a lane, as many lanes as the budget holds ranges. A range's batches take what the
        // budget leaves once its writer and its open inputs are counted, so that one range alone fits it.
        using QueryMemory memory = new QueryMemory(dataset.Session.Options.JobMemoryBudget ?? QueryMemoryBudget.Jobs);
        int batchRows = InputBatchRows(dataset, schema, job, ranges.MostInputs, JobBudget(dataset), writer);
        long perRange = RangeBytes(dataset, schema, job, ranges, writer, batchRows);
        memory.TryGrow(perRange);
        int lanes = 1;
        while (lanes < ranges.Count && memory.TryGrow(perRange))
        {
            lanes++;
        }

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failed = null;
        int next = -1;
        Task[] running = new Task[lanes];
        for (int lane = 0; lane < lanes; lane++)
        {
            running[lane] = Task.Run(LaneAsync, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch when (failed is not null)
        {
            // The failure that stopped the other ranges, rather than how they stopped.
            ExceptionDispatchInfo.Throw(failed);
        }

        long written = 0;
        foreach (long merged in rows)
        {
            written += merged;
        }

        return (streams, written, memory.Peak, lanes);

        async Task LaneAsync()
        {
            try
            {
                for (int range; (range = Interlocked.Increment(ref next)) < ranges.Count;)
                {
                    (streams[range], rows[range]) = await RangeAsync(range, stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                Interlocked.CompareExchange(ref failed, failure, null);
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        async Task<(ObjectStream Outputs, long Rows)> RangeAsync(int range, CancellationToken token)
        {
            for (int doublings = 0; ; doublings++)
            {
                ObjectStream outputs = outputsOf(doublings);
                try
                {
                    long merged = await MergeAsync(dataset, schema, job, outputs, null, null, batchRows, meter, token, ranges, range).ConfigureAwait(false);
                    await outputs.FinishAsync(token).ConfigureAwait(false);
                    return (outputs, merged);
                }
                catch (ObjectPastBoundException) when (doublings < ObjectBound.MostDoublings)
                {
                    await outputs.AbandonAsync().ConfigureAwait(false);
                }
                catch
                {
                    await outputs.AbandonAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
    }
}
