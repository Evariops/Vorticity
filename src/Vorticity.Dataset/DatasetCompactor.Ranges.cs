using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Indexes;

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

    /// <summary>What a range of a consolidation holds, about: <see cref="InputsBytes"/> of the range of the most inputs.</summary>
    private static long RangeBytes(CompactionJob job, KeyRanges ranges) => InputsBytes(job, ranges.MostInputs);

    /// <summary>
    /// What a merge of <paramref name="inputs"/> of the job's inputs holds of them, about: the batches they hold together
    /// (<see cref="InputBatchRows"/>), at <see cref="DecodedPerEncoded"/> times the bytes a row of the job's inputs is
    /// written in.
    /// </summary>
    private static long InputsBytes(CompactionJob job, int inputs)
    {
        long held = (long)inputs * InputBatchRows(inputs);
        return held * Math.Max(job.Bytes / Math.Max(job.Rows, 1), 1) * DecodedPerEncoded;
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
        int granules = 0;
        for (int i = 0; i < indexes.Length; i++)
        {
            CompactionInput input = job.Inputs[i];
            if (schema.Partitioned && !VortexDataset.OrderOf(input.Key).Span.StartsWith(partition))
            {
                return null;
            }

            if (input.Entry.Contents is { KeyIndex: not null } contents
                && await dataset.KeyIndexAsync(input.Entry, contents, cancellationToken).ConfigureAwait(false) is { Granules: > 0 } keys)
            {
                indexes[i] = keys;
                granules += keys.Granules;
            }
        }

        return granules > 0 ? Cut(job, indexes, granules, most, partition) : null;
    }

    /// <summary>The partition <paramref name="input"/>'s rows hold, row-encoded: what leads its tree key.</summary>
    private static byte[] PartitionOf(DatasetSchema schema, CompactionInput input)
    {
        ReadOnlySpan<byte> key = VortexDataset.OrderOf(input.Key).Span;
        return key[..schema.Tree!.PrefixLength(key, 1)].ToArray();
    }

    /// <summary>
    /// Up to <paramref name="most"/> ranges of equal weight over the granules of <paramref name="indexes"/>, and the rows of
    /// each input each reads (<see cref="KeyRangesAsync"/>); null when the granules hold too few keys to cut.
    /// </summary>
    private static KeyRanges? Cut(CompactionJob job, GranuleKeys?[] indexes, int granules, int most, byte[] partition)
    {
        // Every granule, an input's place and the granule's packed in a long, sorted by key.
        long[] order = ArrayPool<long>.Shared.Rent(granules);
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

            Array.Sort(order, 0, granules, new ByGranuleKey(indexes));

            // A cut goes before the first granule whose key follows the one before, once the granules before weigh another
            // share of the whole: shares passed at once over a run of one key make one cut, and one range fewer.
            int shares = 0;
            long before = 0;
            for (int k = 0; k < granules && shares < most - 1; k++)
            {
                int input = (int)(order[k] >> 32);
                ReadOnlySpan<byte> key = indexes[input]!.Key((int)order[k]);
                if (k > 0 && total > 0)
                {
                    int reached = (int)Math.Min(most - 1, before * most / total);
                    if (reached > shares && key.SequenceCompareTo(indexes[(int)(order[k - 1] >> 32)]!.Key((int)order[k - 1])) > 0)
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

            for (int range = 0; range < read.Length; range++)
            {
                long start = range == 0 ? 0 : StartFrom(keys, cuts[range - 1].AsSpan(partition.Length));
                long end = range == cuts.Count ? keys.Rows : EndBefore(keys, cuts[range].AsSpan(partition.Length));
                read[range] = end > start ? new RowRange(start, end) : RowRange.Empty;
            }
        }

        return new KeyRanges([.. cuts], rows);
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
        return granule == keys.Granules && keys.LastKey.AsSpan().SequenceCompareTo(cut) < 0
            ? keys.Rows
            : (long)Math.Max(granule - 1, 0) * keys.GranuleRows;
    }

    /// <summary>The row past the last of an object that may hold a key below <paramref name="cut"/>: the first of the first granule whose key reaches it.</summary>
    private static long EndBefore(GranuleKeys keys, ReadOnlySpan<byte> cut) =>
        Math.Min((long)keys.FirstAtOrPast(cut) * keys.GranuleRows, keys.Rows);

    /// <summary>Granules of several indexes, an index's place and a granule's packed in a long, by key, then by place.</summary>
    private sealed class ByGranuleKey(GranuleKeys?[] indexes) : IComparer<long>
    {
        public int Compare(long left, long right)
        {
            int order = indexes[(int)(left >> 32)]!.Key((int)left).SequenceCompareTo(indexes[(int)(right >> 32)]!.Key((int)right));
            return order != 0 ? order : left.CompareTo(right);
        }
    }

    /// <summary>
    /// The ranges of a consolidation merged side by side, each into objects of its own, as many at once as the session's
    /// memory holds their open inputs, the first unreserved: each range's objects, in the ranges' order, and the rows they
    /// hold together. A range whose object runs past its bound starts again under a bound twice as large, alone; a failure
    /// stops the others, whose objects are given up as a failed job's are.
    /// </summary>
    private static async ValueTask<(ObjectStream[] Streams, long Rows, long Reserved, int AtOnce)> RangesAsync(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        KeyRanges ranges,
        Func<int, ObjectStream> outputsOf,
        CancellationToken cancellationToken)
    {
        ObjectStream[] streams = new ObjectStream[ranges.Count];
        long[] rows = new long[ranges.Count];

        // Each range reserves what it holds before it starts, the first whether the budget grants it or not, as a job in one
        // piece runs; the others wait for a lane, as many lanes as the budget holds ranges.
        using QueryMemory memory = new QueryMemory(dataset.Session.Options.JobMemoryBudget ?? QueryMemoryBudget.Jobs);
        long perRange = RangeBytes(job, ranges);
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
                    long merged = await MergeAsync(dataset, schema, job, outputs, null, null, null, token, ranges, range).ConfigureAwait(false);
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
