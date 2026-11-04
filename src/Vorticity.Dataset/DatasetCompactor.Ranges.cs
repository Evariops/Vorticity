using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Writing;

namespace Vorticity.Dataset;

internal static partial class DatasetCompactor
{
    /// <summary>The most ranges of the key a consolidation runs side by side.</summary>
    private const int MostRanges = 8;

    /// <summary>
    /// What a decoded row's bytes come to over an encoded one's, about: a log's record, its attributes rebuilt from their
    /// shredded keys, read some 360 bytes for the 25 it is written in.
    /// </summary>
    private const long DecodedPerEncoded = 16;

    /// <summary>
    /// What a range of a consolidation holds, about: <see cref="InputsBytes"/> of the range of the most inputs, and its
    /// objects' <paramref name="writer"/> (<see cref="WriterBytes"/>).
    /// </summary>
    private static long RangeBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, KeyRanges ranges, long writer) =>
        InputsBytes(dataset, schema, job, ranges.MostInputs) + writer;

    /// <summary>
    /// What the writer of a job's objects holds, about: each column what it gathers before it writes a chunk, at most
    /// <see cref="VortexFileWriter.MostGatheredPerTarget"/> times the dataset's chunk bytes; a variant column shredded,
    /// <see cref="VariantShredder.HeldPerTarget"/> times its chunk's target. Of the partition values of a job in one
    /// piece, whose objects are written one after the other, the one whose writer holds the most.
    /// </summary>
    private static long WriterBytes(VortexDataset dataset, DatasetSchema schema, IReadOnlyList<ValueShredding>? shreddings)
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
    /// What a merge of <paramref name="inputs"/> of the job's inputs holds of them, about: the batches they hold together
    /// (<see cref="InputBatchRows"/>), at <see cref="DecodedPerEncoded"/> times the bytes a row of the job's inputs is
    /// written in, and <see cref="HeldPerOpenByte"/> times the chunks each holds open (<see cref="OpenChunkBytes"/>).
    /// </summary>
    private static long InputsBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, int inputs)
    {
        long held = (long)inputs * InputBatchRows(inputs);
        return (held * Math.Max(job.Bytes / Math.Max(job.Rows, 1), 1) * DecodedPerEncoded)
            + ((long)inputs * HeldPerOpenByte * OpenChunkBytes(dataset, schema, job));
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
            ObjectEntry entry = input.Entry;
            if (entry.Contents is { ColumnBytes.Count: > 0 } contents)
            {
                for (int column = 0; column < contents.ColumnBytes.Count; column++)
                {
                    long chunks = column < contents.ColumnChunks.Count ? Math.Max(contents.ColumnChunks[column], 1) : 1;
                    sum += contents.ColumnBytes[column] / chunks;
                }
            }
            else
            {
                sum += Math.Min(entry.Bytes, (long)schema.DType.FieldCount * Math.Max(dataset.Options.ChunkBytes, 1));
            }
        }

        return sum / job.Inputs.Count;
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
        Func<int, ObjectStream> outputsOf,
        CancellationToken cancellationToken)
    {
        ObjectStream[] streams = new ObjectStream[ranges.Count];
        long[] rows = new long[ranges.Count];

        // Each range reserves what it holds before it starts, the first whether the budget grants it or not, as a job in one
        // piece runs; the others wait for a lane, as many lanes as the budget holds ranges.
        using QueryMemory memory = new QueryMemory(dataset.Session.Options.JobMemoryBudget ?? QueryMemoryBudget.Jobs);
        long perRange = RangeBytes(dataset, schema, job, ranges, writer);
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
                    long merged = await MergeAsync(dataset, schema, job, outputs, null, null, token, ranges, range).ConfigureAwait(false);
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
