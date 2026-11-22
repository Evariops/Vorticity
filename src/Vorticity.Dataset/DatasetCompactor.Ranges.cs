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
    /// The batches an input of a merge holds at once: the one the merge reads, the one its scan decodes ahead of it, and
    /// the one before, kept until the writers copied the runs they took of it, each with its keys encoded beside it.
    /// </summary>
    internal const int BatchesHeld = 3;

    /// <summary>
    /// What a reader holds of the object of <paramref name="entry"/> open besides its batches, as a plan counts it: its
    /// reader and its footer (<see cref="InputFloorBytes"/>), and of its chunks what its entry says
    /// (<see cref="ObjectEntry.OpenBytes"/>), counted by its writer, or what a job that read it held of it when that is
    /// more (<see cref="VortexDataset.MeasuredOpenBytes"/>); for an object that says nothing, written before entries said
    /// it, its open chunks as its commit says them, encoded: the least it holds, read and guessed at nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count chooses a job; it does not keep one within its memory. Each input reserves what it holds as it reads it
    /// in its file (<see cref="JobMemory.OpenInput"/>), and a job whose inputs hold more than they were counted at waits,
    /// gives back what it started ahead, and at worst ends at a key and commits what it merged. An object that says
    /// nothing was counted at three times its open chunks, a guess the first job of a handle was given up for: 14 MiB
    /// counted an input of log records that held 90 to 150 (2026-11-21).
    /// </para>
    /// <para>
    /// The reader's own is added to what the entry says, which counts the chunks alone: an object that says 4.61 MB open
    /// held 4.76 MB at its first scan, its batches 0.14 of them, and 4.37 MB at the same batches once read before
    /// (2026-11-21).
    /// </para>
    /// </remarks>
    internal static long OpenBytes(VortexDataset dataset, DatasetSchema schema, ObjectEntry entry)
    {
        long known = Math.Max(entry.OpenBytes, dataset.MeasuredOpenBytes(entry.Key));
        return InputFloorBytes + (known > 0 ? known : OpenChunkBytes(dataset, schema, entry));
    }

    /// <summary>
    /// What a row of the object of <paramref name="entry"/> weighs decoded: its rows' canonical bytes over its rows, as its
    /// writer counted them (<see cref="ObjectEntry.DecodedBytes"/>); for an object that does not say, what a row of the
    /// handle's last merge weighed, or the schema's floor (<see cref="RowBytes"/>).
    /// </summary>
    internal static long DecodedRowBytes(VortexDataset dataset, ObjectEntry entry) =>
        entry is { DecodedBytes: > 0, PhysicalRows: > 0 } ? Math.Max(1, entry.DecodedBytes / entry.PhysicalRows) : RowBytesOf(dataset);

    /// <summary>
    /// The window of an input <paramref name="entry"/> of a merge read in batches of <paramref name="batchRows"/> rows: what
    /// the merge holds of it while it has it open, at most. What it holds open (<see cref="OpenBytes"/>), which no batch
    /// lowers, and <see cref="BatchesHeld"/> batches, of its own rows when it holds fewer, a row at what it weighs
    /// decoded (<see cref="DecodedRowBytes"/>) and its key encoded beside it, as long as the object's last one.
    /// </summary>
    internal static long Window(VortexDataset dataset, DatasetSchema schema, ObjectEntry entry, int batchRows) =>
        OpenBytes(dataset, schema, entry)
        + (BatchesHeld * Math.Min(entry.PhysicalRows, batchRows) * (DecodedRowBytes(dataset, entry) + entry.LastKey.Length));

    /// <summary>Whether <paramref name="job"/> merges its inputs on the key: a leveled job, or a tiered one of a dataset with a key; else it reads them one after the other.</summary>
    internal static bool Merges(DatasetSchema schema, CompactionJob job) =>
        job.Style is CompactionStyle.Leveled || (job.Style is CompactionStyle.Tiered && schema.Keyed);

    /// <summary>
    /// What the inputs a merge holds open at once hold together at most, read in batches of <paramref name="batchRows"/>
    /// rows, and how many they are at most: the sum of their windows (<see cref="Window"/>) along the keys, an input open
    /// from its first key to its last (<see cref="OpenSweep"/>). Inputs read one after the other, not
    /// <paramref name="merged"/>, hold the heaviest window alone.
    /// </summary>
    /// <remarks>
    /// A merge opens an input when it reaches its first key and lets go of it past its last (<see cref="KeyOrderedMerge"/>).
    /// The objects one merge wrote lie one after the other along the keys and are never open together: counted all open at
    /// once, as every input of level 0 was, two objects of 145 MB merged into two looked no narrower than before, and a
    /// partition of log records under 2 GiB was merged 262 times without its consolidation ever fitting (2026-11-21).
    /// </remarks>
    internal static (long Bytes, int Open) Peak(
        VortexDataset dataset, DatasetSchema schema, IReadOnlyList<CompactionInput> inputs, bool merged, int batchRows)
    {
        OpenSweep sweep = new OpenSweep(merged);
        if (!merged || Ordered(inputs))
        {
            foreach (CompactionInput input in inputs)
            {
                sweep.Add(input, Window(dataset, schema, input.Entry, batchRows));
            }
        }
        else
        {
            CompactionInput[] ordered = [.. inputs];
            Array.Sort(ordered, static (left, right) => First(left).SequenceCompareTo(First(right)));
            foreach (CompactionInput input in ordered)
            {
                sweep.Add(input, Window(dataset, schema, input.Entry, batchRows));
            }
        }

        return (sweep.Peak, sweep.Most);
    }

    /// <summary>
    /// The places in <paramref name="ordered"/>, a merge's inputs in the order of their first keys, of those open at once
    /// where they hold the most together (<see cref="Peak"/>), read in batches of <paramref name="batchRows"/> rows: the
    /// inputs a merge of some of them first would no longer hold open together.
    /// </summary>
    internal static List<int> OpenAtPeak(VortexDataset dataset, DatasetSchema schema, IReadOnlyList<CompactionInput> ordered, int batchRows)
    {
        OpenSweep sweep = new OpenSweep(merged: true);
        foreach (CompactionInput input in ordered)
        {
            sweep.Add(input, Window(dataset, schema, input.Entry, batchRows));
        }

        // Those added up to the one that made the peak, and not ended before its first key.
        List<int> open = [];
        if (sweep.PeakAt < 0)
        {
            return open;
        }

        ReadOnlySpan<byte> at = First(ordered[sweep.PeakAt]);
        for (int i = 0; i <= sweep.PeakAt; i++)
        {
            ReadOnlySpan<byte> last = ordered[i].Entry.LastKey.Span;
            if (last.IsEmpty || last.SequenceCompareTo(at) >= 0)
            {
                open.Add(i);
            }
        }

        return open;
    }

    /// <summary>The first key of <paramref name="input"/>, row-encoded: its tree key without its uid.</summary>
    private static ReadOnlySpan<byte> First(CompactionInput input) => VortexDataset.OrderOf(input.Key).Span;

    /// <summary>Whether <paramref name="inputs"/> come in the order of their first keys, as the objects of one level do in the order of its tree.</summary>
    private static bool Ordered(IReadOnlyList<CompactionInput> inputs)
    {
        for (int i = 1; i < inputs.Count; i++)
        {
            if (First(inputs[i]).SequenceCompareTo(First(inputs[i - 1])) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The inputs a merge holds open at once as it goes along the keys, added in the order of their first keys: an input
    /// leaves once the one added starts past its last key, and what the inputs open then hold together is kept at its
    /// most. An input that states no last key stays open to the end. Not merged, inputs are read one after the other, and
    /// the heaviest is kept.
    /// </summary>
    private sealed class OpenSweep(bool merged)
    {
        // The windows of the inputs open, by their last keys, the first to end at the top.
        private readonly PriorityQueue<long, ReadOnlyMemory<byte>> _open = new PriorityQueue<long, ReadOnlyMemory<byte>>(LastKeys.Instance);
        private long _held;
        private int _added;

        /// <summary>The most the inputs open at once held together.</summary>
        internal long Peak { get; private set; }

        /// <summary>The most inputs open at once.</summary>
        internal int Most { get; private set; }

        /// <summary>The place, among the inputs added, of the one whose opening first made <see cref="Peak"/>; -1 before any.</summary>
        internal int PeakAt { get; private set; } = -1;

        /// <summary>Adds <paramref name="input"/>, which holds <paramref name="window"/> open, past those added before it; the most held since.</summary>
        internal long Add(CompactionInput input, long window)
        {
            int place = _added++;
            if (!merged)
            {
                Most = 1;
                if (window > Peak)
                {
                    Peak = window;
                    PeakAt = place;
                }

                return Peak;
            }

            ReadOnlySpan<byte> first = First(input);
            while (_open.TryPeek(out long gone, out ReadOnlyMemory<byte> last) && !last.IsEmpty && last.Span.SequenceCompareTo(first) < 0)
            {
                _open.Dequeue();
                _held -= gone;
            }

            _open.Enqueue(window, input.Entry.LastKey);
            _held += window;
            Most = Math.Max(Most, _open.Count);
            if (_held > Peak)
            {
                Peak = _held;
                PeakAt = place;
            }

            return Peak;
        }

        /// <summary>Last keys in their order, an object that states none after every other: it ends with the merge.</summary>
        private sealed class LastKeys : IComparer<ReadOnlyMemory<byte>>
        {
            internal static readonly LastKeys Instance = new LastKeys();

            public int Compare(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
                left.IsEmpty || right.IsEmpty ? left.IsEmpty.CompareTo(right.IsEmpty) : left.Span.SequenceCompareTo(right.Span);
        }
    }

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
        WriterHeld(dataset, WriterBytes(dataset, schema, job, shreddings));

    /// <summary>
    /// What a writer whose columns gather <paramref name="gathered"/> bytes holds at most: that times what a writer holds
    /// past its columns (<see cref="JobShares.WriterPerGathered"/>), within the writer's share.
    /// </summary>
    /// <remarks>
    /// A writer's share bounds what its columns gather, not what it holds. It reserves the copies whose size it counted
    /// and waits for what it has in the air before it starts more (<see cref="JobMemory"/>), gives back the blocks its
    /// arenas keep while the job does not hold it, and reserves the leaves of a shredded chunk it lays out at once, which
    /// brought a consolidation's writer under 2 GiB at degree 14 from 357 MiB to 267 to 285 of a share of 269. A writer
    /// that gathers to its ceiling was counted, until then, at what the writer of the handle's last job had held when
    /// that was more: a measure of one job counted for the next, the last of them (2026-11-22).
    /// </remarks>
    private static long WriterHeld(VortexDataset dataset, long gathered) =>
        Math.Min(JobShares.Of(JobBudget(dataset)).Writer, (long)(gathered * JobShares.WriterPerGathered));

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

    /// <summary>
    /// The fewest rows an input's batch holds, however tight the budget: under a block of 8 192 rows, the decoders decode a
    /// range of a chunk all the same. The backlog bench's consolidation of 9 million rows merged 345 000 rows a second from 74
    /// inputs under 2 GiB, at the narrowest batches the budget left it, and 263 000 from 300 inputs under 8 GiB, at a block
    /// and more: no cost of the short batches showed beside the width's (2026-11-16).
    /// </summary>
    internal const int LeastBatchRows = 1_024;

    /// <summary>
    /// The rows of an input's batch in a merge of <paramref name="inputs"/> of the job's inputs, a row of the batch holding
    /// <paramref name="rowBytes"/>, whose batches may hold <paramref name="share"/> bytes
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
    /// The rows of an input's batch in a merge of <paramref name="job"/>'s inputs whose windows may hold
    /// <paramref name="share"/> bytes together, <paramref name="streams"/> merges of them running at once, a slice and
    /// those priced ahead of it: the most rows, a power of two between <see cref="LeastBatchRows"/> and a window, at which
    /// the inputs open at once hold no more (<see cref="Peak"/>), and no more than <see cref="MergeRows"/> together.
    /// </summary>
    /// <remarks>
    /// Counted from the inputs' entries alone, before any is opened: the same rows at any degree and on any run. They
    /// followed what the handle's last job had measured an input to hold, and a job repriced them down as its inputs
    /// opened, by what the open ones held then.
    /// </remarks>
    internal static int InputBatchRows(VortexDataset dataset, DatasetSchema schema, CompactionJob job, bool merged, int streams, long share)
    {
        streams = Math.Max(1, streams);
        (_, int open) = Peak(dataset, schema, job.Inputs, merged, LeastBatchRows);
        long byCount = Math.Max(1, MergeRows / ((long)Math.Max(1, open) * streams));
        int rows = Math.Clamp(1 << BitOperations.Log2((ulong)Math.Min(byCount, int.MaxValue)), LeastBatchRows, FlatLayoutReader.WindowRows);
        while (rows > LeastBatchRows && Peak(dataset, schema, job.Inputs, merged, rows).Bytes > share / streams)
        {
            rows >>= 1;
        }

        return rows;
    }

    /// <summary>
    /// What the job holds at least: its inputs open at once at the fewest rows a job reads them in
    /// (<see cref="LeastBatchRows"/>, <see cref="Peak"/>), and its writer (<see cref="WriterCost"/>).
    /// Priced at a block of 8 192 rows, a consolidation of 194 inputs under 4 GiB was said not to fit, and its partition
    /// was merged by class first and rewritten twice, 59 s, where the consolidation ran at smaller batches within its
    /// budget, 1 222 MiB of 1 344 (2026-11-16).
    /// </summary>
    internal static long LeastBytes(VortexDataset dataset, CompactionJob job)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        return Peak(dataset, schema, job.Inputs, Merges(schema, job), LeastBatchRows).Bytes + WriterCost(dataset, schema, job, null);
    }

    /// <summary>
    /// Whether the job is counted to hold within the jobs' budget at its least (<see cref="LeastBytes"/>): what a plan asks
    /// before it chooses it. Its inputs counted at what their entries say, and at what a job that read them held of them
    /// when that is more (<see cref="OpenBytes"/>): a job ended short of room is counted wider the next time, by what its
    /// inputs' files said, where a job refused a block had the next take half as many inputs whatever they held.
    /// </summary>
    internal static bool Fits(VortexDataset dataset, CompactionJob job) =>
        LeastBytes(dataset, job) <= JobBudget(dataset);

    /// <summary>
    /// The rows of an input's batch in the first pass of a job, which counts its inputs one a lane: what a lane's share of the
    /// budget holds of them.
    /// </summary>
    private static int CensusRows(VortexDataset dataset, CompactionJob job) =>
        InputBatchRows(1, BatchesHeld * RowBytesOf(dataset), JobBudget(dataset) / Math.Max(1, dataset.Session.Options.MaxDegreeOfParallelism));

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
    /// (<see cref="JobShares.Gathered"/>), halved each time a job of the handle ended short of room with nothing it could
    /// commit and nothing learned of its inputs (<see cref="VortexDataset.JobGatherHalvings"/>), a mebibyte at least.
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
    /// What the writer of a job of <paramref name="rows"/> rows holds at most, as <see cref="WriterCost"/> counts it for a
    /// job whose shreddings are not chosen yet: what a plan counts beside the inputs it takes.
    /// </summary>
    private static long PlannedWriterCost(VortexDataset dataset, DatasetSchema schema, long rows) =>
        WriterHeld(dataset, Math.Min(Math.Min(TargetsBytes(dataset, schema, null), rows * RowBytesOf(dataset)), GatherCeiling(dataset)));

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
    /// merge takes: <paramref name="least"/> whatever the bounds but what a merge's memory holds (<see cref="MemoryWidth"/>),
    /// two at least, then as many more as keep their bytes within
    /// <paramref name="bytes"/>, the layout's object size or a caller's bound, and what the merge holds within the jobs'
    /// budget, the inputs open at once at their windows (<see cref="Window"/>, <see cref="OpenSweep"/>), the writer
    /// at what it is counted to hold (<see cref="PlannedWriterCost"/>). The whole class when it fits: one job where a
    /// merge of the fan-out at a time took 469 of them to empty a class of 3 283 small objects.
    /// </summary>
    internal static int TierWidth(VortexDataset dataset, IReadOnlyList<CompactionInput> inputs, int least, long bytes)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long budget = JobBudget(dataset);
        bytes = Math.Min(bytes, JobBytesBound(dataset));

        // The fan-out at least, but no more than a merge's memory is counted to hold: a merge due when its class reached
        // the fan-out took it whatever its bounds.
        least = Math.Min(least, Math.Max(2, MemoryWidth(dataset, inputs)));
        OpenSweep open = new OpenSweep(schema.Keyed);
        long rows = 0;
        long sum = 0;
        int taken = 0;
        foreach (CompactionInput input in inputs)
        {
            // In the order of the tree, the inputs' first keys': what the inputs open at once hold with this one, at the
            // shortest batches a job reads them in, which it lengthens as far as its room holds once it has its inputs.
            ObjectEntry entry = input.Entry;
            long held = open.Add(input, Window(dataset, schema, entry, LeastBatchRows));
            if (taken >= least && (sum + entry.Bytes > bytes || held + PlannedWriterCost(dataset, schema, rows + entry.Rows) > budget))
            {
                break;
            }

            rows += entry.Rows;
            sum += entry.Bytes;
            taken++;
        }

        return taken;
    }

    /// <summary>
    /// How many of <paramref name="inputs"/>, in the order of the tree, a merge takes within the jobs' budget, by memory
    /// alone: those open at once at their windows (<see cref="Window"/>, <see cref="OpenSweep"/>), and the writer beside
    /// them, as <see cref="TierWidth"/> counts them; one at least. Inputs that lie one after the other along the keys are
    /// open one at a time, and a merge takes as many of them as there are. The bytes a job reads, which the scratch and the
    /// window bound, are the plan's to judge, not this.
    /// </summary>
    internal static int MemoryWidth(VortexDataset dataset, IReadOnlyList<CompactionInput> inputs)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        long budget = JobBudget(dataset);
        OpenSweep open = new OpenSweep(schema.Keyed);
        long rows = 0;
        int taken = 0;
        foreach (CompactionInput input in inputs)
        {
            ObjectEntry entry = input.Entry;
            long held = open.Add(input, Window(dataset, schema, entry, LeastBatchRows));
            if (taken >= 1 && held + PlannedWriterCost(dataset, schema, rows + entry.Rows) > budget)
            {
                break;
            }

            rows += entry.Rows;
            taken++;
        }

        return taken;
    }

    /// <summary>
    /// Ranges of the key, which a merge cuts its rows in: the slices of one stream (<see cref="SlicedMerge"/>), or the parts
    /// below a piece's cut. The cuts between them are keys of the tree's order that lead keys rather than hold whole ones,
    /// with the rows of each input each range reads.
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
}
