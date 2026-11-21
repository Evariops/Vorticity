using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Writing;

namespace Vorticity.Dataset;

internal static partial class DatasetCompactor
{
    /// <summary>The most slices a merge is cut in.</summary>
    private const int MostSlices = 64;

    /// <summary>The most ranges a merge is cut in, each writing objects of its own (<see cref="KeyRanges.RangesOf"/>).</summary>
    private const int MostRanges = 8;

    /// <summary>
    /// The slices ahead of the one being taken a sliced merge prices its inputs' batches for, whatever the degree, so that
    /// the batches, and the runs they cut, are the same at any degree: the slices that merge ahead are as many as lanes free
    /// and this, whichever is fewer.
    /// </summary>
    private const int SlicesAheadPriced = 3;

    /// <summary>
    /// What an open input holds besides its batch, at least: its reader, its footer parsed, the segments of the chunks it
    /// reads. Measured at 0.10 to 0.15 MB an input, linear from 8 to 3 283 open inputs of 24 KB (2026-11-13).
    /// </summary>
    internal const long InputFloorBytes = 128 << 10;

    /// <summary>
    /// What an input is guessed to hold for each byte of the batch it hands the merge, before a job of the handle measured
    /// it (<see cref="VortexDataset.InputJobBytes"/>): the batch, the chunks it was cut from, decoded whole, and what its
    /// columns' reshaping keeps. 3.3 on a metric's records, 1.78 KB of resident memory a row for 530 bytes of batch, over
    /// merges of 8 inputs of 13 000 to 53 000 rows (2026-11-13); a first guess, which the first job replaces by its measure.
    /// </summary>
    private const long FirstGuessPerBatchByte = 3;

    /// <summary>
    /// What an input of a job of the dataset holds for each row of its batch: as the handle's last job measured it, its
    /// input's blocks past an input's floor over the rows of its batch; before any, the first guess times a row's weight.
    /// </summary>
    private static long HeldPerRow(VortexDataset dataset) =>
        dataset.InputJobBytes > 0
            ? Math.Max(RowBytesOf(dataset), (dataset.InputJobBytes - InputFloorBytes) / Math.Max(dataset.JobBatchRows, 1))
            : FirstGuessPerBatchByte * RowBytesOf(dataset);

    /// <summary>
    /// What an input <paramref name="entry"/> of a job holds open in a merge, read in batches of
    /// <paramref name="batchRows"/>: the chunks it keeps open, <see cref="InputFloorBytes"/> at least, and its batch, its own
    /// rows when it holds fewer, at what a row of a batch held in the handle's last job (<see cref="HeldPerRow"/>). A small
    /// input costs what it holds.
    /// </summary>
    internal static long InputCost(VortexDataset dataset, DatasetSchema schema, ObjectEntry entry, int batchRows) =>
        Math.Max(InputFloorBytes, OpenChunkBytes(dataset, schema, entry)) + (Math.Min(entry.PhysicalRows, batchRows) * HeldPerRow(dataset));

    /// <summary>
    /// What the writer of <paramref name="job"/>'s objects holds at most: what its columns gather (<see cref="WriterBytes"/>)
    /// times what a writer holds past them (<see cref="JobShares.WriterPerGathered"/>), within the writer's share of the
    /// jobs' budget (<see cref="JobShares"/>), which a writer that gathers to its ceiling fills.
    /// </summary>
    /// <remarks>
    /// It was what the handle's last job measured its writer to hold, and before any, a writer that shreds variants was
    /// priced at five times its gathering ceiling: the first job of a handle priced at the ceiling alone held four times it
    /// and was refused twice (2026-11-17), and a measure of one job priced the next, whatever its shape.
    /// </remarks>
    internal static long WriterCost(VortexDataset dataset, DatasetSchema schema, CompactionJob job, IReadOnlyList<ValueShredding>? shreddings) =>
        Math.Min(
            JobShares.Of(JobBudget(dataset)).Writer,
            (long)(WriterBytes(dataset, schema, job, shreddings) * JobShares.WriterPerGathered));

    /// <summary>
    /// What a decoded row of the job's inputs weighs, about: what the handle's last merge measured, or, before any, the
    /// least its schema's columns take decoded (<see cref="FloorBytes"/>). The bytes a row is written in mislead on small
    /// objects, whose chunks' fixed costs make a row of four 51 KB.
    /// </summary>
    internal static long RowBytes(VortexDataset dataset, CompactionJob job) => RowBytesOf(dataset);

    /// <summary>What a decoded row of the dataset weighs, about (<see cref="RowBytes"/>).</summary>
    private static long RowBytesOf(VortexDataset dataset) =>
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

    /// <summary>The rows of the batch an input is measured at before a handle's first job: a block.</summary>
    private const int MeasuredBatchRows = 8_192;

    /// <summary>
    /// Measures, once a handle, before its first merge, what an input of <paramref name="job"/> holds in a merge at batches
    /// of <see cref="MeasuredBatchRows"/> rows, and what a row weighs decoded: its input of the most rows merged alone under a
    /// job's memory of its own until the merge hands its first batch, which the handle keeps as a job's measure
    /// (<see cref="VortexDataset.LearnInputCost"/>). Before it, a handle priced an input at three times a row's weight: a
    /// sealed input of 0.6 MB holds 4 MB at its first batch, its segment deciphered and its chunks decompressed whole, and
    /// the first job of a backlog of 300 objects took them all, was refused at 1.3 GB under a jobs' budget of 1 344 MiB, then
    /// again at half, before it ran on 75 (2026-11-15).
    /// </summary>
    /// <remarks>
    /// Through the merge and not the input's scan alone: an input of 1 924 rows held 78 KB read alone, 566 KB in a merge
    /// of eight, its keys encoded and its batch sized for the job's, and a consolidation priced by the scan fitted the
    /// budget, then was refused twice.
    /// </remarks>
    /// <returns>Whether it measured; false when the budget refused the input, which the job then meets as it runs.</returns>
    internal static async ValueTask<bool> MeasureInputAsync(VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        int largest = 0;
        for (int i = 1; i < job.Inputs.Count; i++)
        {
            if (job.Inputs[i].Entry.PhysicalRows > job.Inputs[largest].Entry.PhysicalRows)
            {
                largest = i;
            }
        }

        RowMeter meter = new RowMeter();
        JobMemory memory = new JobMemory(dataset);
        await using (memory.ConfigureAwait(false))
        {
            try
            {
                // An input of a merge holds two batches, the one the merge reads and the one its scan decodes ahead: the rows
                // of both are read as one batch, with none decoded ahead, so that what the input holds is theirs alone. Read
                // as two, the second decoded while the merge took the first, the peak was the two or more by what their
                // decodes held at once: 15.7 MiB or 20.4 to 21.4 MiB for the largest input of a consolidation of 23 inputs
                // of log records, from one run to the next, which then read its inputs in batches of 16 384 rows or of 8 192,
                // its two ranges one after the other or side by side, and ran in 6.0 to 6.2 s or in 6.5 s (2026-11-19).
                KeyOrderedMerge merge = InputsMerged(
                    dataset, schema, job, 2 * MeasuredBatchRows, cancellationToken, only: largest, meter: meter, memory: memory, prefetch: 0);
                await using (merge.ConfigureAwait(false))
                {
                    if (!await merge.MoveNextAsync().ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }
            catch (VortexMemoryException) when (memory.Refused && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (dataset.DecodedRowBytes == 0 && meter.BytesPerRow > 0)
            {
                dataset.LearnDecodedRowBytes(meter.BytesPerRow);
            }

            dataset.LearnInputCost(memory.InputsPeak, MeasuredBatchRows);
            return true;
        }
    }

    /// <summary>
    /// What an open input of the job holds besides its batch, about: the chunks it holds open
    /// (<see cref="OpenChunkBytes(VortexDataset, DatasetSchema, CompactionJob)"/>), and <see cref="InputFloorBytes"/> at least.
    /// What they decode to is in what a row of its batch holds, as the handle's last job measured it (<see cref="HeldPerRow"/>).
    /// </summary>
    internal static long OpenBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job) =>
        Math.Max(InputFloorBytes, OpenChunkBytes(dataset, schema, job));

    /// <summary>
    /// The fewest rows an input's batch holds, however tight the budget: under a block of 8 192 rows, the decoders decode a
    /// range of a chunk all the same. The backlog bench's consolidation of 9 million rows merged 345 000 rows a second from 74
    /// inputs under 2 GiB, at the narrowest batches the budget left it, and 263 000 from 300 inputs under 8 GiB, at a block
    /// and more: no cost of the short batches showed beside the width's (2026-11-16).
    /// </summary>
    internal const int LeastBatchRows = 1_024;

    /// <summary>
    /// The rows of an input's batch in a merge of <paramref name="inputs"/> of the job's inputs, a row of the batch holding
    /// <paramref name="rowBytes"/> (<see cref="HeldPerRow"/>), whose batches may hold <paramref name="share"/> bytes
    /// together: what the share holds of each, by bytes alone, a power of two between <see cref="LeastBatchRows"/> and a
    /// window (<see cref="FlatLayoutReader.WindowRows"/>). Rows of a metric's maps and lists weigh four times a log's: by
    /// rows alone, eight inputs of a window each held 1.7 GB of batches, and a floor of a block held 4.7 GB for 201 inputs.
    /// </summary>
    internal static int InputBatchRows(int inputs, long rowBytes, long share)
    {
        // The share bounds the batches by bytes when the budget is tight; a large one gives no more than MergeRows together,
        // what a merge needs to run long, rather than all it could take.
        long byCount = MergeRows / Math.Max(1, inputs);
        long byBytes = share / ((long)Math.Max(1, inputs) * Math.Max(rowBytes, 1));
        long rows = Math.Max(Math.Min(byCount, byBytes), 1);
        return Math.Clamp(1 << BitOperations.Log2((ulong)Math.Min(rows, int.MaxValue)), LeastBatchRows, FlatLayoutReader.WindowRows);
    }

    /// <summary>
    /// The rows a merge's inputs hold at once together, at most, however large the budget: each input holds a batch of its
    /// rows while the others step, so a merge of many inputs reads each in smaller batches than a merge of a few. A million
    /// rows of a log's columns, their attributes rebuilt, held some 360 MB as eight inputs of a window each; without the
    /// bound, a budget of half a machine of 48 GB gave each input a window, and a run of the suite held 37 GB.
    /// </summary>
    private const int MergeRows = 1 << 20;

    /// <summary>
    /// The rows of an input's batch in a merge of <paramref name="inputs"/> of the job's inputs under a budget of
    /// <paramref name="budget"/> bytes, of which its writer holds <paramref name="writer"/>: what is left once the inputs'
    /// open bytes are taken goes to their batches (<see cref="InputBatchRows(int, long, long)"/>).
    /// </summary>
    internal static int InputBatchRows(VortexDataset dataset, DatasetSchema schema, CompactionJob job, int inputs, long budget, long writer)
    {
        // After a refusal, half the batch the refused job read at most (VortexDataset.JobSizingCap).
        int rows = InputBatchRows(inputs, HeldPerRow(dataset), budget - writer - ((long)inputs * OpenBytes(dataset, schema, job)));
        return dataset.JobSizingCap.BatchRows is > 0 and int cap ? Math.Min(rows, cap) : rows;
    }

    /// <summary>
    /// What the job holds at least, its inputs' batches at the fewest rows a job reads them in (<see cref="LeastBatchRows"/>,
    /// <see cref="InputCost"/>): its level 0 inputs, which overlap and are all open at once, one input of the levels above,
    /// which are disjoint and open one after another, and its writer (<see cref="WriterCost"/>). Priced at a block of 8 192
    /// rows, a consolidation of 194 inputs under 4 GiB was said not to fit, and its partition was merged by class first and
    /// rewritten twice, 59 s, where the consolidation ran at smaller batches within its budget, 1 222 MiB of 1 344
    /// (2026-11-16).
    /// </summary>
    internal static long LeastBytes(VortexDataset dataset, CompactionJob job)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long held = 0;
        long above = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            long cost = InputCost(dataset, schema, input.Entry, LeastBatchRows);
            if (input.Level == 0)
            {
                held += cost;
            }
            else
            {
                above = Math.Max(above, cost);
            }
        }

        return held + above + WriterCost(dataset, schema, job, null);
    }

    /// <summary>
    /// Whether the job holds within the jobs' budget at its least (<see cref="LeastBytes"/>), and opens no more inputs of
    /// level 0 at once than a job refused since lets the next take (<see cref="VortexDataset.JobWidthCap"/>). Priced alone, a
    /// consolidation of eight inputs refused at 8.45 MB under 8 MiB fitted again: its inputs' cost was a chunk each, 566 KB
    /// at batches of 8 192 rows as of 16 384, where the price scaled it by the rows, and it was refused twice.
    /// </summary>
    internal static bool Fits(VortexDataset dataset, CompactionJob job)
    {
        int cap = dataset.JobWidthCap;
        if (cap > 0)
        {
            int open = 0;
            foreach (CompactionInput input in job.Inputs)
            {
                open += input.Level == 0 ? 1 : 0;
            }

            if (open > cap)
            {
                return false;
            }
        }

        return LeastBytes(dataset, job) <= JobBudget(dataset);
    }

    /// <summary>
    /// The rows of an input's batch in the first pass of a job, which counts its inputs one a lane: what a lane's share of the
    /// budget holds of them.
    /// </summary>
    private static int CensusRows(VortexDataset dataset, CompactionJob job) =>
        InputBatchRows(1, HeldPerRow(dataset), JobBudget(dataset) / Math.Max(1, dataset.Session.Options.MaxDegreeOfParallelism));

    /// <summary>
    /// The bytes of the jobs' memory budget of the handle's session: what the jobs may hold together, which a job's blocks
    /// never pass (<see cref="JobMemory"/>).
    /// </summary>
    internal static long JobBudget(VortexDataset dataset) =>
        QueryMemoryBudget.JobsOf(dataset.Session).CeilingBytes;

    /// <summary>
    /// The share of the retention window, one in this many, a job or a piece of a consolidation commits within at the bytes
    /// a second the handle's last job read: a commit refuses objects begun half the window ago, and the other quarter is
    /// for a slower turn.
    /// </summary>
    private const int WindowShare = 4;

    /// <summary>
    /// The most bytes a job of the handle reads, by what it can commit in time and what its scratch holds: a quarter of the
    /// retention window at the rate of the handle's last job (<see cref="VortexDataset.JobBytesPerSecond"/>), and what the
    /// session's scratch leaves, which a job of large inputs copies them to (<see cref="JobInputs"/>). No bound before a job
    /// measured its rate, nor of the scratch below the bytes a job reads through it from.
    /// </summary>
    internal static long JobBytesBound(VortexDataset dataset)
    {
        long bound = long.MaxValue;
        if (dataset.JobBytesPerSecond > 0)
        {
            TimeSpan window = DatasetVacuum.WindowOf(dataset.Snapshot.Header.Retention);
            bound = (long)Math.Min(long.MaxValue / 2, dataset.JobBytesPerSecond * window.TotalSeconds / WindowShare);
        }

        return Math.Min(bound, Math.Max(ScratchRoom(dataset), JobInputs.FromBytes - 1));
    }

    /// <summary>What the session's scratch leaves a job's copies: its budget's room, or nine tenths of its directory's free space.</summary>
    private static long ScratchRoom(VortexDataset dataset)
    {
        if (dataset.Session.Options.ScratchBudget is { } budget)
        {
            return Math.Max(0, budget.CeilingBytes - budget.ReservedBytes);
        }

        string scratch = ScratchDirectories.Of(dataset.Session.Options.ScratchDirectory);
        return GroupCore.Free(scratch) is long free ? free - (free / 10) : long.MaxValue;
    }


    /// <summary>
    /// What the writer of a job's objects holds, about: each column what it gathers before it writes a chunk, at most
    /// <see cref="VortexFileWriter.MostGatheredPerTarget"/> times the dataset's chunk bytes; a variant column shredded,
    /// <see cref="VariantShredder.HeldPerTarget"/> times its chunk's target. Of the partition values of a job in one
    /// piece, whose objects are written one after the other, the one whose writer holds the most. No more, though, than
    /// the job's rows decoded (<see cref="RowBytes"/>), which the writer holds once at most: a job of 200 000 rows of
    /// metrics was priced 2.6 GB for chunks it never filled, and held 380 MB in all.
    /// </summary>
    private static long WriterBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, IReadOnlyList<ValueShredding>? shreddings) =>
        Math.Min(Math.Min(TargetsBytes(dataset, schema, shreddings), job.Rows * RowBytes(dataset, job)), GatherCeiling(dataset));

    /// <summary>The least a job's writer gathers its columns in together: a mebibyte, the chunk a column aims at.</summary>
    internal const long LeastGatherCeiling = 1L << 20;

    /// <summary>
    /// What a job's writer gathers its columns in together at most, past which a column writes its chunk early
    /// (<see cref="VortexWriteOptions.GatheredBytesCeiling"/>): what the writer's share of the jobs' budget leaves its columns
    /// (<see cref="JobShares.Gathered"/>), halved each time the job before it was refused with nothing else left to narrow
    /// (<see cref="VortexDataset.JobGatherHalvings"/>), a mebibyte at least.
    /// </summary>
    /// <remarks>
    /// It was an eighth of the jobs' budget, set by the backlog bench under 2 GiB, where a half, a quarter, an eighth and a
    /// sixteenth ran 25 jobs in 60 s, 17 in 55, 11 in 49 and 8 in 48, the last's object 2.3 % larger, its chunks written
    /// early (2026-11-16). A writer holds some 185 MiB past its columns at degree 14, its flights and its lanes' workspaces,
    /// and 2.3 times its ceiling: 361 to 385 MiB at an eighth of a jobs' budget of 672, 277 to 291 at a sixteenth
    /// (2026-11-20). Its share of two fifths leaves its columns 60 MiB there.
    /// </remarks>
    internal static long GatherCeiling(VortexDataset dataset) =>
        Math.Max(LeastGatherCeiling, JobShares.Of(JobBudget(dataset)).Gathered >> dataset.JobGatherHalvings);

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
    /// What a merge of <paramref name="inputs"/> of the job's inputs holds of them, about: each what it costs open
    /// (<see cref="InputCost"/>), its batch of <paramref name="batchRows"/> rows or of its own rows when it holds fewer. The
    /// inputs that cost the most are counted, which a merge of the job's inputs that overlap holds open at once: a small input
    /// costs what it holds, where a batch of 8 192 rows each priced 3 305 inputs of four rows at 400 GB.
    /// </summary>
    private static long InputsBytes(VortexDataset dataset, DatasetSchema schema, CompactionJob job, int inputs, int batchRows)
    {
        int count = Math.Min(inputs, job.Inputs.Count);
        long[] costs = new long[job.Inputs.Count];
        for (int i = 0; i < costs.Length; i++)
        {
            costs[i] = InputCost(dataset, schema, job.Inputs[i].Entry, batchRows);
        }

        if (count < costs.Length)
        {
            Array.Sort(costs);
        }

        long held = 0;
        for (int i = costs.Length - count; i < costs.Length; i++)
        {
            held += costs[i];
        }

        return held;
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
    /// merge takes: <paramref name="least"/> whatever the bounds but what a merge holds open at once (<see cref="MemoryWidth"/>),
    /// two at least, then as many more as keep their bytes within
    /// <paramref name="bytes"/>, the layout's object size or a caller's bound, and what the merge holds within the jobs'
    /// budget, each input open at once at what an input of the handle's last job held (<see cref="InputCost"/>), the writer
    /// at what it held (<see cref="WriterCost"/>); and no more than half a job the budget refused
    /// (<see cref="VortexDataset.JobWidthCap"/>). The whole class when it fits: one job where a merge of the fan-out at a time
    /// took 469 of them to empty a class of 3 283 small objects.
    /// </summary>
    internal static int TierWidth(VortexDataset dataset, IReadOnlyList<CompactionInput> inputs, int least, long bytes)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long budget = JobBudget(dataset);
        bytes = Math.Min(bytes, JobBytesBound(dataset));
        int cap = dataset.JobWidthCap;
        least = cap > 0 ? Math.Min(least, Math.Max(2, cap)) : least;

        // The fan-out at least, but no more than a merge holds open at once: a merge due when its class reached the fan-out
        // took it whatever its bounds, and was refused when its memory did not hold it, then narrowed.
        least = Math.Min(least, Math.Max(2, MemoryWidth(dataset, inputs)));
        long rowBytes = RowBytesOf(dataset);
        long targets = TargetsBytes(dataset, schema, null);
        long held = 0;
        long rows = 0;
        long sum = 0;
        int taken = 0;
        foreach (CompactionInput input in inputs)
        {
            ObjectEntry entry = input.Entry;
            long cost = InputCost(dataset, schema, entry, 8_192);
            long writer = Math.Max(dataset.WriterJobBytes, Math.Min(targets, (rows + entry.Rows) * rowBytes));
            if (taken >= least && (sum + entry.Bytes > bytes || held + cost + writer > budget || (cap > 0 && taken >= cap)))
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
    /// How many of <paramref name="inputs"/> a merge holds open at once within the jobs' budget, by memory alone: each at
    /// what an input of the handle's last job, or its measure, held (<see cref="InputCost"/>), and the writer beside them, as
    /// <see cref="TierWidth"/> counts them; one at least. The bytes a job reads, which the scratch and the window bound, are
    /// the plan's to judge, not this.
    /// </summary>
    internal static int MemoryWidth(VortexDataset dataset, IReadOnlyList<CompactionInput> inputs)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long budget = JobBudget(dataset);
        long rowBytes = RowBytesOf(dataset);
        long targets = TargetsBytes(dataset, schema, null);
        long held = 0;
        long rows = 0;
        int taken = 0;
        foreach (CompactionInput input in inputs)
        {
            ObjectEntry entry = input.Entry;
            long cost = InputCost(dataset, schema, entry, MeasuredBatchRows);
            long writer = Math.Max(dataset.WriterJobBytes, Math.Min(targets, (rows + entry.Rows) * rowBytes));
            if (taken >= 1 && held + cost + writer > budget)
            {
                break;
            }

            held += cost;
            rows += entry.Rows;
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
        /// <summary>
        /// The slices a merge of <paramref name="job"/> is cut in, by its rows and its inputs' bytes and never the degree, so
        /// that its runs are the same at any degree: one a <see cref="DatasetOptions.SliceRows"/>, at most
        /// <see cref="MostSlices"/>, and no more than half the chunks an input holds of a column, about; fewer than two for
        /// none.
        /// </summary>
        /// <remarks>
        /// A slice opens every input whose rows it holds and reads the chunks its range touches: at each cut, each such input's
        /// chunk of each column is read by the two slices it falls in, so slices add about a chunk a column an input each, and
        /// as many slices as an input holds chunks of a column read it twice over. Sixty appends of 30 000 rows, a chunk a
        /// column each, cut in six slices by their rows, opened every append in every slice: four slices at once on fourteen
        /// lanes held 671 MiB of inputs under a jobs' budget of 672 before the writer took a row, and the job was given up
        /// (2026-11-17). Half the chunks keeps the reads within one and a half times a merge's in one piece.
        /// </remarks>
        internal static int SlicesOf(VortexDataset dataset, CompactionJob job)
        {
            long byRows = job.Rows / Math.Max(1, dataset.Options.SliceRows);
            int columns = Math.Max(1, dataset.Snapshot.Schema.DType.FieldCount);
            long perInput = job.Bytes / Math.Max(1, job.Inputs.Count);
            long chunks = perInput / ((long)columns * Math.Max(1, dataset.Options.ChunkBytes));
            return (int)Math.Clamp(Math.Min(byRows, chunks / 2), 0, MostSlices);
        }

        /// <summary>
        /// The ranges a merge of <paramref name="job"/> is cut in, each writing objects of its own: the most that the objects
        /// its inputs weigh divide into evenly, one a <see cref="DatasetOptions.RangeRows"/> of its rows at most, rounded,
        /// and <see cref="MostRanges"/> at most; never the degree, so that its objects are the same at any degree. Fewer than
        /// two for none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A range ends its last object where its rows end, whatever the size its objects aim at. Cut by its rows alone, a
        /// consolidation of 200 million metric records aimed at 256 MiB wrote eight objects of 107 MiB, one a range, and
        /// one of 51.8 million aimed at 1 GiB three objects for 248 MB. A whole number of objects a range, by what the
        /// inputs weigh: a job of an object at most runs as one range and writes one object, and one of three objects as
        /// three ranges of an object each.
        /// </para>
        /// <para>
        /// What the ranges are worth, measured on those jobs at degree 14 once a writer's line cut its chunks column by
        /// column (2026-11-20): 2.1 s in three ranges for 2.2 to 2.3 s in one under 16 GiB on metric records, 2.9 s for
        /// 2.7 to 2.8 s under 2 GiB, where the ranges go one after another, and 6.3 to 6.5 s in two ranges for 6.1 to
        /// 6.2 s in one on log records.
        /// </para>
        /// </remarks>
        internal static int RangesOf(VortexDataset dataset, CompactionJob job)
        {
            long rows = Math.Max(1, dataset.Options.RangeRows);
            long byRows = Math.Clamp((job.Rows + (rows / 2)) / rows, 0, MostRanges);
            long objects = ObjectsOf(job);
            for (long ranges = Math.Min(byRows, objects); ranges >= 2; ranges--)
            {
                if (objects % ranges == 0)
                {
                    return (int)ranges;
                }
            }

            return 1;
        }

        /// <summary>The objects the inputs of <paramref name="job"/> weigh, at the size its objects aim at, rounded up.</summary>
        internal static long ObjectsOf(CompactionJob job)
        {
            long target = Math.Max(1, job.TargetBytes);
            return Math.Max(1, (job.Bytes + target - 1) / target);
        }

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
    /// The slices of the key a merge cuts its rows into, of equal weight, <paramref name="most"/> at most (<see cref="KeyRanges.SlicesOf"/>);
    /// null for one. The cuts are first keys of the inputs' granules, which their sparse indexes give, a granule weighing its
    /// object's bytes over its granules: the rows of a key, and of an indexed prefix, all fall in one slice, which reads of
    /// each input the granules that may hold its keys alone. Null too when the inputs keep no index, or hold more than one
    /// partition.
    /// </summary>
    private static async ValueTask<KeyRanges?> KeyRangesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, int most, CancellationToken cancellationToken)
    {
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
        Block<long> order = Blocks.Rent<long>(granules);

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

            order.AsSpan(0, granules).Sort(new ByGranuleKey(decoded));

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
            Blocks.Return(order);
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
    /// The ranges of a merge, each into objects of its own (<see cref="KeyRanges.RangesOf"/>), side by side on the caller
    /// and on lanes free, as many at once as the room the jobs' budget leaves the job holds what a range costs, its writer
    /// and its inputs open, one at least: each range's objects, in the ranges' order; the rows they hold together; the ranges
    /// at once; the rows of an input's batch; and the ranges begun again under a doubled bound. A range whose object runs
    /// past its bound begins again under a bound twice as large, alone; a failure stops the others, and every object written
    /// is given up.
    /// </summary>
    /// <remarks>
    /// The objects are the same at any degree: the ranges, their cuts and the batches their inputs read are the job's, and
    /// each range's objects are its own stream's. A range's stream holds no lane of the job's own (<see cref="ObjectStream.Lane"/>),
    /// which two writers waiting for the store would each give back.
    /// </remarks>
    private static async ValueTask<(ObjectStream[] Streams, long Rows, int AtOnce, int BatchRows, int Restarts)> RangesAsync(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        KeyRanges ranges,
        long writer,
        RowMeter meter,
        JobMemory memory,
        Func<int, ObjectStream> outputsOf,
        CancellationToken cancellationToken)
    {
        int count = ranges.Count;
        ObjectStream?[] streams = new ObjectStream?[count];
        long[] rows = new long[count];
        int restarts = 0;

        // Each range's writer gathers within the job's ceiling over the ranges (ObjectStream.Ranges), and is priced at the job's
        // writer over them: the writers of every range together take what one writer of the job would. The batches take what
        // the budget leaves once the writers and every range's inputs open are counted, so that the ranges fit side by side,
        // whatever the degree; as many ranges at once as the room holds at that price. Priced at a whole writer each, five
        // times the gathering ceiling before a job measured it, a range took 7.2 GiB of a jobs' budget of 10.5, and the
        // ranges of a consolidation of metric records went one after another; their batches sized for one range alone, the
        // two ranges of a consolidation of log records did too (2026-11-18).
        long perWriter = Math.Max(1, writer / count);
        int batchRows = InputBatchRows(dataset, schema, job, ranges.MostInputs * count, JobBudget(dataset), writer);
        long perRange = perWriter + InputsBytes(dataset, schema, job, ranges.MostInputs, batchRows);

        // A quarter of a range's price left over: two ranges of a consolidation of metric records under 2 GiB, priced at the
        // jobs' budget of 672 MiB together, held 684, their inputs a third past their price and their writers below it, and
        // the job was refused; with that room kept, it runs its ranges one after another, and three at once under 16 GiB.
        int atOnce = (int)Math.Clamp((memory.Room - (perRange / 4)) / perRange, 1, count);

        // After a refusal, no more inputs open at once than the refused job held.
        if (dataset.JobSizingCap.Open is > 0 and int open)
        {
            atOnce = Math.Clamp(open / Math.Max(1, ranges.MostInputs), 1, atOnce);
        }

        memory.Sizing = (batchRows, ranges.MostInputs * atOnce);
        memory.WriterPrice = perWriter * atOnce;

        // The ranges that merged at once, as the lanes free let them, which the job reports.
        int running = 0;
        int peak = 0;
        try
        {
            await Execution.LaneWorkers.ForAsync(
                dataset.Session.Lanes,
                count,
                atOnce,
                async (range, token) =>
                {
                    int now = Interlocked.Increment(ref running);
                    for (int seen = Volatile.Read(ref peak); now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen; seen = Volatile.Read(ref peak))
                    {
                    }

                    try
                    {
                        await MergeRangeAsync(range, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The ranges written before the failure are given up with it.
            foreach (ObjectStream? finished in streams)
            {
                if (finished is not null)
                {
                    await finished.AbandonAsync().ConfigureAwait(false);
                }
            }

            throw;
        }

        long total = 0;
        ObjectStream[] written = new ObjectStream[count];
        for (int range = 0; range < count; range++)
        {
            total += rows[range];
            written[range] = streams[range]!;
        }

        return (written, total, Math.Max(peak, 1), batchRows, restarts);

        // A range into objects of its own, begun again alone under a doubled bound when an object passes it.
        async ValueTask MergeRangeAsync(int range, CancellationToken token)
        {
            for (int doublings = 0; ; doublings++)
            {
                ObjectStream outputs = outputsOf(doublings);
                try
                {
                    rows[range] = await MergeAsync(
                        dataset, schema, job, outputs, null, null, batchRows, meter, memory, token, ranges, range, null, perWriter * atOnce).ConfigureAwait(false);
                    await outputs.FinishAsync(token).ConfigureAwait(false);
                    streams[range] = outputs;
                    return;
                }
                catch (ObjectPastBoundException) when (doublings < ObjectBound.MostDoublings)
                {
                    await outputs.AbandonAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref restarts);
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
