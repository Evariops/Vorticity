// A job's writer sends the runs its merge staged through on a lane of their own while the merge stages the next ones: the
// objects a consolidation writes on four lanes are those it writes on one, cut at the same rows, holding the same rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetCompactionLanesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two days of documents in blocks of 1 024 rows, consolidated on one lane and on four. On four, the writers send
    /// staged blocks through on a lane of their own; on one, none. The objects are cut at the same rows either way, the size
    /// a writer reads to cut them taken before a staged block goes through, whichever lane it goes through on; and they hold
    /// the same rows. Their bytes are compared on copies of one store (<see cref="AConsolidationWritesTheSameSegmentsAtAnyDegreeAndOnAnyRun"/>):
    /// here each run appends objects of fresh identities, which order the objects of equal keys in the tree.
    /// </summary>
    [Fact]
    public async Task AConsolidationOnFourLanesCutsItsObjectsAtTheRowsItCutsThemOnOne()
    {
        (List<long> alone, List<Doc> aloneRows, long aloneFlown) = await ConsolidateAsync(lanes: 1);
        (List<long> across, List<Doc> acrossRows, long acrossFlown) = await ConsolidateAsync(lanes: 4);
        Assert.Equal(0, aloneFlown);
        Assert.True(acrossFlown > 0, "no staged block went through on a lane of its own");
        Assert.True(alone.Count >= 2, $"{alone.Count} objects");
        Assert.Equal(alone, across);
        Assert.Equal(aloneRows.Count, acrossRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, acrossRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(acrossRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    /// <summary>
    /// A consolidation on a session of four lanes, three of them held by other work: the job's census, its blocks and its
    /// chunks go through on the one left and on its own thread, and it ends with the objects it writes on one lane.
    /// </summary>
    [Fact]
    public async Task AConsolidationLeftOneLaneOfFourEndsWithTheObjectsOfOne()
    {
        (List<long> alone, List<Doc> aloneRows, _) = await ConsolidateAsync(lanes: 1);
        (List<long> left, List<Doc> leftRows, _) = await ConsolidateAsync(lanes: 4, held: 3);
        Assert.Equal(alone, left);
        Assert.Equal(aloneRows.Count, leftRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, leftRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(leftRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    /// <summary>
    /// A consolidation's merge keeps each input's batch as its scan decoded it, its keys encoded beside the decode, until
    /// the writers have copied the runs they keep of it: it takes every batch over from its scan, copies none, and encodes
    /// no key on its own thread, at one lane and at four. A consolidation in pieces leaves deletion vectors on its inputs,
    /// which the decode applies.
    /// </summary>
    [Fact]
    public async Task AConsolidationsMergeTakesItsInputsBatchesOverAndCopiesNone()
    {
        foreach (int lanes in new[] { 1, 4 })
        {
            Random random = new Random(31);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, OptionsOf(session), Ct);
            for (int a = 0; a < 6; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
            Assert.True(results.Sum(static result => result.InputWindowsTaken) > 0, $"at {lanes} lanes, no batch was taken over");
            Assert.Equal(0, results.Sum(static result => result.InputWindowsCopied));
            Assert.Equal(0, results.Sum(static result => result.InputWindowsKeyedByMerge));

            // The merge hands its writers a block's map at a time, short as an object ends, and never a run: runs of a few
            // rows, the services of two days interleaved, made thousands of calls.
            long calls = results.Sum(static result => result.WriterBatches);
            long merged = results.Sum(static result => result.Rows);
            Assert.True(merged >= calls * 256, $"at {lanes} lanes, {calls} calls of the writers for {merged} rows");
            Assert.Equal(48_000, await dataset.ScanBuilder().CountAsync(Ct));
        }
    }

    /// <summary>
    /// One store of six appends, copied four times and consolidated on each copy at one lane, one lane again, four lanes
    /// and fourteen: every segment of every object is the same bytes. The rows of one key come in the order of the job's
    /// inputs whatever the batches its inputs are read in, which the memory the job measures decides; an input that ran
    /// through the keys equal to another's wrote them first or not depending on where its batch ended. Cut in slices of
    /// 2 048 rows, the merge is the same at any degree too, its slices merged ahead on the lanes free or by the job's flow.
    /// Cut in ranges of 12 000 rows, each range writing objects of its own, side by side past one lane, it is too.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2_048, 0)]
    [InlineData(0, 12_000)]
    public async Task AConsolidationWritesTheSameSegmentsAtAnyDegreeAndOnAnyRun(int sliceRows, int rangeRows)
    {
        // Blocks of 256 rows, so that an object is aimed at dozens of them and its writer sends several through at once.
        List<CompactionResult> sliced = await SameSegmentsAtDegreesAsync(
            [1, 1, 4, 14],
            static random => $"svc-{random.Next(0, 60):D2}",
            session =>
            {
                // Slices of a few thousand rows over inputs of chunks of 4 KiB, which they cut a column's chunks of: a merge
                // takes no more slices than half the chunks an input holds of a column.
                DatasetOptions options = OptionsOf(session) with { Write = new VortexWriteOptions { BlockRows = 256 } };
                options = rangeRows > 0 ? options with { RangeRows = rangeRows } : options;
                return sliceRows > 0 ? options with { SliceRows = sliceRows, ChunkBytes = 4 << 10 } : options;
            });

        // At four lanes and fourteen, the writers sent blocks through their line, several at once, and the bytes are those of
        // one; with chunks of 4 KiB, a block is a chunk of its own, which goes through alone.
        if (rangeRows > 0)
        {
            // A day of some 24 000 rows in two ranges, their objects apart, merged side by side past one lane.
            Assert.True(sliced.Max(static job => job.ObjectsOut) >= 2, "no job wrote its ranges' objects apart");
            Assert.True(sliced.Max(static job => job.RangesAtOnce) >= 2, "no ranges merged side by side");
        }
        else if (sliceRows == 0)
        {
            Assert.True(sliced.Sum(static job => job.WriterBlocksOnLine) > 0, "no block went through a writer's line");
        }
        else
        {
            Assert.True(sliced.Max(static job => job.MergeSlices) >= 2, "no merge was cut in slices");
            Assert.True(sliced.Sum(static job => job.MergeSlicesAhead) > 0, "no slice was merged ahead");

            // Each block a chunk of 4 KiB alone, cut column by column past one lane, the blocks after it going through.
            Assert.True(sliced.Sum(static job => job.WriterChunksCutOnLine) > 0, "no chunk was cut column by column on a writer's line");
        }
    }

    /// <summary>
    /// Six appends of two days into objects aimed at a gibibyte, consolidated with a range each 12 000 rows: a day, some
    /// 24 000 rows that weigh far less than an object, is merged as one range into one object. Cut by its rows alone, in
    /// two ranges, a day wrote two objects, one a range, whatever the size its objects aim at.
    /// </summary>
    [Fact]
    public async Task AMergeOfAnObjectsWorthAtMostRunsAsOneRange()
    {
        Random random = new Random(29);
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = OptionsOf(VortexSession.Default);
        options = options with { Layout = options.Layout! with { ObjectBytes = 1L << 30 }, RangeRows = 12_000 };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, options, Ct);
        for (int a = 0; a < 6; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        string ran = string.Join(", ", results.Select(static job => $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut}, {job.Rows} rows, {job.RangesAtOnce} ranges at once"));
        Assert.True(results.Count == 2, ran);
        Assert.All(results, job => Assert.True(job.Rows > 12_000 * 3 / 2 && job.ObjectsOut == 1 && job.RangesAtOnce == 1, ran));
        Assert.Equal(48_000, await dataset.ScanBuilder().CountAsync(Ct));
    }

    /// <summary>
    /// Log records, a nullable message beside a variant of some forty keys, most of them sparse, in twelve appends a day over
    /// two days, merged by size class: the tier merges write the same segments at one lane, four and fourteen, their
    /// variant shredded, their blocks through their writers' lines past one lane.
    /// </summary>
    [Fact]
    public async Task ATierMergeOfLogRecordsWritesTheSameSegmentsAtAnyDegree()
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(41);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, LogDoc.Schema, LogOptionsOf(VortexSession.Default, LayoutOrganization.Tiered), Ct);
            for (int a = 0; a < 24; a++)
            {
                await AppendLogsAsync(dataset, random, a * 1_500, 1_500, latePercent: 0, dayRows: 18_000);
            }
        }

        List<CompactionResult> jobs = await SameObjectsAtDegreesAsync(
            built, [1, 4, 14], static session => LogOptionsOf(session, LayoutOrganization.Tiered), static dataset => DrainAsync(dataset));
        string ran = string.Join(", ", jobs.Select(static job => $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut} line {job.WriterBlocksOnLine}"));
        Assert.True(jobs.Exists(static job => job.Trigger == CompactionTrigger.Tier), ran);
        Assert.True(jobs.Sum(static job => job.WriterBlocksOnLine) > 0, ran);
    }

    /// <summary>
    /// Log records consolidated once, then appended to and consolidated again with the job's inputs handing its writers
    /// what they took of the variant's rows as their own shreddings hold them (<see cref="DatasetOptions.TakesVariants"/>),
    /// at four lanes and at fourteen: the segments are those of the consolidation whose writers read every row put back
    /// together, at one lane and at four. The second consolidation reads the objects the first shredded, whose rows it
    /// takes. Blocks of 256 rows, so that a run of a block's holds four rows or more on average and is worth taking, in
    /// chunks of some kilobytes, dozens an object: an input takes the chunks it decodes once a first block was worth it,
    /// which a file decoded whole before any block never does. The inputs hand their chunks over not put back together.
    /// In chunks of 4 KiB a block makes a chunk, which the writer's line cuts column by column, and no value of a block
    /// that took its runs is ever put back. In chunks of 96 KiB a chunk takes several blocks, which wait whole in
    /// transit, their values put back first by what the block took.
    /// </summary>
    /// <remarks>
    /// A key is held by half the records or by one in four hundred, so that both jobs shred the same keys: an input that
    /// types a key its job does not is read as its rows put back together, a window at a time.
    /// </remarks>
    [Theory]
    [InlineData(4 << 10)]
    [InlineData(96 << 10)]
    public async Task AConsolidationWhoseInputsHandOverWhatTheyTookOfAVariantWritesTheSameSegments(int chunkBytes)
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(43);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, LogDoc.Schema, TakenLogOptionsOf(VortexSession.Default, takes: false, chunkBytes), Ct);
            for (int a = 0; a < 12; a++)
            {
                await AppendLogsAsync(dataset, random, a * 3_000, 3_000, latePercent: 0, dayRows: 96_000, SteadyAttributes);
            }

            await dataset.ConsolidateAsync(Ct);
            for (int a = 12; a < 16; a++)
            {
                await AppendLogsAsync(dataset, random, a * 3_000, 3_000, latePercent: 0, dayRows: 96_000, SteadyAttributes);
            }
        }

        bool[] takes = [false, false, true, true];
        int run = 0;
        List<CompactionResult> jobs = await SameObjectsAtDegreesAsync(
            built, [1, 4, 4, 14], session => TakenLogOptionsOf(session, takes[run++], chunkBytes), static async dataset => [.. await dataset.ConsolidateAsync(Ct)]);
        string ran = string.Join(", ", jobs.Select(static job =>
            $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut} line {job.WriterBlocksOnLine} taken {job.WriterBlocksOfRunsTaken} put back {job.WriterBlocksPutBack} apart {job.WriterColumnsPutBackApart} chunks {job.InputVariantChunks}"));
        Assert.True(jobs.Sum(static job => job.WriterBlocksOnLine) > 0, ran);
        Assert.True(jobs.Sum(static job => job.WriterBlocksOfRunsTaken) > 0, ran);

        // Chunks handed over not put back together, and blocks written from them. Where each block makes a chunk, the line
        // cuts it column by column and its values never are put back. Where a chunk takes several blocks, they wait whole
        // in transit, their values put back first by what the block took.
        Assert.True(jobs.Sum(static job => job.InputVariantChunks.Held) > 0, ran);
        long putBack = jobs.Sum(static job => job.WriterBlocksPutBack);
        if (chunkBytes == 4 << 10)
        {
            Assert.True(putBack == 0, ran);
        }
        else
        {
            Assert.True(putBack > 0, ran);
        }
    }

    /// <summary>
    /// A log record's attributes whose keys every job of some thousands of records shreds alike: six keys every record has,
    /// six half of them have, and ten one in four hundred has, too few for any job to shred.
    /// </summary>
    private static Variant SteadyAttributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(30)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(800)}")),
            ("host", Variant.From($"host-{random.Next(16)}")),
            ("region", Variant.From(random.Next(3) == 0 ? "eu" : "us")),
        ];
        for (int key = 0; key < 6; key++)
        {
            if (random.Next(2) == 0)
            {
                fields.Add(($"k{key:D2}", key % 3 == 0 ? Variant.From(random.Next(1_000)) : Variant.From($"v{random.Next(50)}")));
            }
        }

        for (int key = 0; key < 10; key++)
        {
            if (random.Next(400) == 0)
            {
                fields.Add(($"r{key:D2}", key % 2 == 0 ? Variant.From(random.Next(100_000)) : Variant.From($"rare-{random.Next(9)}")));
            }
        }

        return Variant.Object([.. fields]);
    }

    private static DatasetOptions TakenLogOptionsOf(VortexSession session, bool takes, int chunkBytes)
    {
        // Objects aimed at 2 MiB, which an append of 3 000 records stays well under and is merged.
        DatasetOptions options = LogOptionsOf(session, LayoutOrganization.Tiered);
        return options with
        {
            Write = new VortexWriteOptions { BlockRows = 256 },
            Layout = options.Layout! with { ObjectBytes = 1 << 21 },
            ChunkBytes = chunkBytes,
            TakesVariants = takes,
        };
    }

    /// <summary>
    /// The same records over a leveled tree of one day, a few of every append a little late, behind the rows level 1 already
    /// holds, and nine appends left in level 0, past its ceiling: the exits of level 0, which keep the late rows apart,
    /// write the same segments at one lane, four and fourteen.
    /// </summary>
    [Fact]
    public async Task ExitsOfLevelZeroWithLateRowsWriteTheSameSegmentsAtAnyDegree()
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(43);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, LogDoc.Schema, LogOptionsOf(VortexSession.Default, LayoutOrganization.Leveled), Ct);
            for (int a = 0; a < 19; a++)
            {
                await AppendLogsAsync(dataset, random, a * 1_500, 1_500, latePercent: 3, dayRows: 1_000_000);
                if (a < 10)
                {
                    await DrainAsync(dataset);
                }
            }
        }

        List<CompactionResult> jobs = await SameObjectsAtDegreesAsync(
            built, [1, 4, 14], static session => LogOptionsOf(session, LayoutOrganization.Leveled), static dataset => DrainAsync(dataset));
        string ran = string.Join(", ", jobs.Select(static job => $"{job.Trigger} L{job.FromLevel}->L{job.ToLevel} {job.ObjectsIn}->{job.ObjectsOut}"));
        Assert.True(jobs.Exists(static job => job.Trigger == CompactionTrigger.LevelZeroCeiling), ran);
    }

    /// <summary>
    /// Six appends of log records whose keys interleave closely, every append holding each service's instants a sixth apart,
    /// and some of every append deleted before the consolidation: the merge hands its writer runs of one row, each of another
    /// input than the run before, which the writer copies a window of each input's batch at a time and gathers into its
    /// blocks. The consolidation holds every record kept, each with its own message, null where it was, and attributes.
    /// </summary>
    [Fact]
    public async Task AMergeOfInputsWhoseKeysInterleaveKeepsEveryRowWithItsValues()
    {
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, LogDoc.Schema, LogOptionsOf(VortexSession.Default, LayoutOrganization.Tiered), Ct);
        List<LogDoc> kept = [];
        for (int a = 0; a < 6; a++)
        {
            LogDoc[] logs = new LogDoc[3_000];
            for (int i = 0; i < logs.Length; i++)
            {
                long at = (i * 6L) + a;
                logs[i] = new LogDoc(
                    0,
                    $"svc-{i % 3:D2}",
                    random.Next(4) == 0 ? null : $"request {random.Next(1_000)} served in {random.Next(500)} ms",
                    at,
                    LogAttributes(random));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<LogDoc>(logs, Ct);
            await dataset.AppendAsync(draft, Ct);
            kept.AddRange(logs.Where(static log => log.At is not (>= 4_000 and < 6_000) and not (>= 11_000 and < 11_030)));
        }

        await dataset.DeleteAsync<LogDoc>(r => (r.At >= 4_000 & r.At < 6_000) | (r.At >= 11_000 & r.At < 11_030), Ct);
        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        Assert.NotEmpty(results);

        List<LogDoc> read = [.. (await dataset.Scan<LogDoc>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static log => log.At)];
        kept.Sort(static (x, y) => x.At.CompareTo(y.At));
        Assert.Equal(kept.Count, read.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            Assert.Equal(kept[i].At, read[i].At);
            Assert.Equal(kept[i].Service, read[i].Service);
            Assert.Equal(kept[i].Message, read[i].Message);
            Assert.True(kept[i].Attributes.Equals(read[i].Attributes), $"record {kept[i].At}");
        }
    }

    /// <summary>
    /// Thirty-two appends of log records merged by tiers into objects aimed at 2 MiB: each merge reads less than an
    /// object holds and writes one object, and the plan runs out of jobs. The variant column, shredded, is written in
    /// chunks of a million rows, so none of it is encoded before an object of some ten thousand rows ends, and its writer
    /// counts it at the size of its rows as they come, six times what they encode to: ended at that size, a merge of
    /// five objects wrote five again, which the plan took again without end.
    /// </summary>
    [Fact]
    public async Task ATierMergeWhoseInputsHoldLessThanAnObjectWritesOne()
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        Random random = new Random(43);
        DatasetOptions options = TakenLogOptionsOf(VortexSession.Default, takes: false, 4 << 10);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(built, LogDoc.Schema, options, Ct);
        for (int a = 0; a < 32; a++)
        {
            await AppendLogsAsync(dataset, random, a * 3_000, 3_000, latePercent: 0, dayRows: 96_000, SteadyAttributes);
        }

        // Twelve jobs at most: the plan asks for three, and asked for one more without end.
        List<CompactionResult> jobs = [];
        for (int step = 0; step < 12 && await dataset.CompactAsync(cancellationToken: Ct) is { } job; step++)
        {
            jobs.Add(job);
        }

        string ran = string.Join(", ", jobs.Select(static job => $"{job.Trigger} {job.ObjectsIn}->{job.ObjectsOut} {job.BytesIn}->{job.BytesOut} B"));
        Assert.NotEmpty(jobs);
        Assert.All(jobs, job =>
        {
            Assert.True(job.Trigger == CompactionTrigger.Tier && job.BytesIn * ObjectBound.OutPerIn <= options.Layout!.ObjectBytes, ran);
            Assert.True(job.ObjectsOut == 1, ran);
        });
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
    }

    /// <summary>
    /// Thirty appends of 12 000 log records of one day, consolidated into objects aimed at 2 MiB: every object but the
    /// last weighs nine tenths of the size at least, its segments, its columns' chunks, its zone maps and its statistics,
    /// pass the size by a twentieth at most, and the objects are the same at one lane, four and fourteen. The variant
    /// column, shredded, aims its chunks at a million rows, so that an object holds none of it encoded when its rows as
    /// they came reach the size, at a sixth of it: the job has the column write what it holds then, and goes on at the
    /// rate that chunk encoded to. Without it, each object ended there.
    /// </summary>
    /// <remarks>
    /// Two bounds and not one: at 256 rows a block the indexes an object writes at its end weigh from half its rows to
    /// three times them, one of them kept in an object and given up in the next, so that an object ends past its size by
    /// what its own end weighs over the lightest end the objects before it wrote.
    /// </remarks>
    [Fact]
    public async Task AConsolidationEndsEachObjectAtItsSize()
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(43);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, LogDoc.Schema, TakenLogOptionsOf(VortexSession.Default, takes: false, 4 << 10), Ct);
            for (int a = 0; a < 30; a++)
            {
                await AppendLogsAsync(dataset, random, a * 12_000, 12_000, latePercent: 0, dayRows: 1_000_000, SteadyAttributes);
            }
        }

        List<(long Bytes, long Segments)[]> weighed = [];
        await SameObjectsAtDegreesAsync(
            built, [1, 4, 14], static session => TakenLogOptionsOf(session, takes: false, 4 << 10), async dataset =>
            {
                List<CompactionResult> jobs = [.. await dataset.ConsolidateAsync(Ct)];
                List<(long, long)> objects = [];
                await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
                {
                    byte[] bytes = new byte[positioned.Entry.Bytes];
                    await dataset.Store.ReadAsync(positioned.Entry.Key, 0, bytes, null, Ct);
                    await using VortexFile file = await VortexSession.Default.OpenAsync(new Vorticity.IO.MemorySegmentSource(bytes), cancellationToken: Ct);
                    objects.Add((bytes.Length, file.SegmentMap.Sum(static segment => (long)segment.Length)));
                }

                weighed.Add([.. objects]);
                return jobs;
            });

        const long Size = 1 << 21;
        string sizes = string.Join(", ", weighed[0].Select(static o => $"{o.Bytes} ({o.Segments} of segments)"));
        Assert.True(weighed[0].Length >= 4, sizes);
        Assert.All(weighed[0][..^1], o => Assert.True(o.Bytes >= Size * 0.9 && o.Segments <= Size * 1.05, sizes));
    }

    /// <summary>Every job the plan gives, run until none is left.</summary>
    private static async Task<List<CompactionResult>> DrainAsync(VortexDataset dataset)
    {
        List<CompactionResult> jobs = [];
        for (int step = 0; await dataset.CompactAsync(cancellationToken: Ct) is { } job; step++)
        {
            Assert.True(
                step < 1_000,
                "compaction never runs out of jobs, the last: "
                    + string.Join(", ", jobs.TakeLast(6).Select(static last => $"{last.Trigger} {last.FromLevel}->{last.ToLevel} {last.ObjectsIn}->{last.ObjectsOut} {last.Rows} rows {last.BytesIn}->{last.BytesOut} B")));
            jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>
    /// Appends <paramref name="rows"/> log records from instant <paramref name="from"/> on, a millisecond apart, a day each
    /// <paramref name="dayRows"/>, <paramref name="latePercent"/> of them a little behind the last.
    /// </summary>
    private static async Task AppendLogsAsync(
        VortexDataset dataset, Random random, long from, int rows, int latePercent, long dayRows, Func<Random, Variant>? attributes = null)
    {
        attributes ??= LogAttributes;
        LogDoc[] logs = new LogDoc[rows];
        for (int i = 0; i < rows; i++)
        {
            long at = from + i;
            if (from > 0 && random.Next(100) < latePercent)
            {
                at = Math.Max(0, from - random.Next(1, 2_000));
            }

            logs[i] = new LogDoc(
                (int)(at / dayRows),
                $"svc-{random.Next(0, 12):D2}",
                random.Next(4) == 0 ? null : $"request {random.Next(1_000)} served in {random.Next(500)} ms",
                at,
                attributes(random));
        }

        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<LogDoc>(logs, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    /// <summary>A log record's attributes: six keys every record has, and of thirty-four more, each now and then, the later the rarer.</summary>
    private static Variant LogAttributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(30)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(800)}")),
            ("host", Variant.From($"host-{random.Next(16)}")),
            ("region", Variant.From(random.Next(3) == 0 ? "eu" : "us")),
        ];
        for (int key = 0; key < 34; key++)
        {
            if (random.Next(key + 2) == 0)
            {
                fields.Add(($"k{key:D2}", key % 3 == 0 ? Variant.From(random.Next(1_000)) : Variant.From($"v{random.Next(50)}")));
            }
        }

        return Variant.Object([.. fields]);
    }

    private static DatasetOptions LogOptionsOf(VortexSession session, LayoutOrganization organization) => new DatasetOptions
    {
        Session = session,
        Seed = 0x1067_0001,

        // Blocks of 64 rows, so that an object is aimed at dozens of them and its writer sends several through its line.
        Write = new VortexWriteOptions { BlockRows = 64 },
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(LogDoc.Day)),
            SortKey = [LayoutValue.Column(nameof(LogDoc.Service)), LayoutValue.Column(nameof(LogDoc.At))],
            Organization = organization,
            ObjectBytes = 1 << 19,
            MergeFanout = 4,
            CalmAfter = null,
        },
    };

    /// <summary>
    /// Two appends of 150 000 rows, each read in several batches: the lanes work ahead of the merge, the input it reads
    /// decoding windows past its own two, or the slices of a piece after the one taken merging, and the rows are those of a
    /// consolidation on one lane.
    /// </summary>
    [Fact]
    public async Task AConsolidationsMergeDecodesTheInputItReadsAheadOnTheLanes()
    {
        // The appends made once and copied for each degree, with their identities, which order the rows of one key.
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(37);
            await using VortexDataset appended = await VortexDataset.CreateAsync(built, Doc.Schema, OptionsOf(VortexSession.Default), Ct);
            for (int a = 0; a < 2; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 150_000, 150_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
                await using ObjectDraft draft = appended.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await appended.AppendAsync(draft, Ct);
            }
        }

        List<long> objects = [];
        long ahead = 0;
        foreach (int lanes in new[] { 1, 4 })
        {
            await using MemoryObjectStore store = await CopyAsync(built);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, OptionsOf(session), Ct);

            // Ahead on the lanes free: windows of the input the merge drains, or slices of a piece merged ahead, which come
            // first, the merge being downstream of the reads.
            IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
            long taken = results.Sum(static result => result.InputWindowsAhead + result.MergeSlicesAhead);
            Assert.True(lanes > 1 || taken == 0, $"at one lane, {taken} windows or slices ahead");
            ahead += taken;
            List<long> sizes = [];
            await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                sizes.Add(positioned.Entry.Rows);
            }

            if (lanes == 1)
            {
                objects = sizes;
            }
            else
            {
                Assert.Equal(objects, sizes);
            }

            Assert.Equal(300_000, await dataset.ScanBuilder().CountAsync(Ct));
        }

        Assert.True(ahead > 0, "no input decoded a window ahead past its own, and no slice was merged ahead");
    }

    /// <summary>
    /// A consolidation over a store whose puts answer a little later: the job's flow gives its lane back while its objects
    /// wait for the store, and takes it again after, and the objects hold the rows they hold over a store that answers at once.
    /// </summary>
    [Fact]
    public async Task AJobGivesItsLaneBackWhileItsObjectsWaitForTheStore()
    {
        // The appends made once and copied for each pass, with their identities, which order the objects of equal keys in the
        // tree, and so the rows of one key: appended anew, a pass wrote its rows of one service in another order.
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(41);
            await using VortexDataset appended = await VortexDataset.CreateAsync(built, Doc.Schema, OptionsOf(VortexSession.Default), Ct);
            for (int a = 0; a < 4; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
                await using ObjectDraft draft = appended.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await appended.AppendAsync(draft, Ct);
            }
        }

        List<long> objects = [];
        foreach (bool late in new[] { false, true })
        {
            await using MemoryObjectStore memory = await CopyAsync(built);
            await using LatePuts store = new LatePuts(memory, late);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, OptionsOf(session), Ct);
            IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
            int waits = results.Sum(static result => result.StoreWaitsWithoutLane);
            Assert.True(late ? waits > 0 : waits == 0, $"{waits} waits for the store without the job's lane, its puts late: {late}");
            List<long> sizes = [];
            await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                sizes.Add(positioned.Entry.Rows);
            }

            if (!late)
            {
                objects = sizes;
            }
            else
            {
                Assert.Equal(objects, sizes);
            }

            Assert.Equal(32_000, await dataset.ScanBuilder().CountAsync(Ct));
        }
    }

    /// <summary>
    /// A job's advances, its flows' units ahead of what they must do: admitted while their part's share holds them past what
    /// the part holds (<see cref="JobShares"/>) and the room the jobs' budget leaves holds them, a window decoded ahead and a
    /// slice merged ahead past what its writer was priced at and does not hold yet, a block in flight past nothing; refused,
    /// and counted, past their share or once other work holds the room.
    /// </summary>
    [Fact]
    public async Task AJobAdmitsItsUnitsAheadWhileItsRoomHoldsThemTheWriterFirst()
    {
        Vorticity.QueryMemoryBudget jobs = new Vorticity.QueryMemoryBudget(64L << 20);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.JobMemoryBudget = jobs);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, OptionsOf(session), Ct);
        JobMemory memory = new JobMemory(dataset);
        await using (memory.ConfigureAwait(false))
        {
            memory.WriterPrice = 16L << 20;
            Assert.Equal(new JobShares(Inputs: 40_265_320, MergeAhead: 40_265_320, Writer: 26_843_544, GroupsWaiting: 0), memory.Shares);

            // Room for all: 64 MiB free. The inputs' share, 38.4 MiB, holds a slice of 32 and not one of 40; the writer's,
            // 25.6 MiB, a block of 8 and not one of 30.
            Assert.True(memory.AdmitsRead(8L << 20));
            Assert.True(memory.AdmitsWrite(8L << 20));
            Assert.True(memory.AdmitsMerge(32L << 20));
            Assert.False(memory.AdmitsMerge(40L << 20));
            Assert.False(memory.AdmitsWrite(30L << 20));

            // Other work holds 44 MiB: 20 left, of which the writer may take 16 more; a window of 8 waits, a block goes.
            Assert.True(jobs.TryReserve(44L << 20));
            try
            {
                Assert.False(memory.AdmitsRead(8L << 20));
                Assert.False(memory.AdmitsMerge(8L << 20));
                Assert.True(memory.AdmitsWrite(8L << 20));
                Assert.True(memory.AdmitsRead(4L << 20));
                Assert.Equal((1, 2, 1), memory.HeldBack);
            }
            finally
            {
                jobs.Release(44L << 20);
            }
        }
    }

    /// <summary>
    /// A consolidation of appends whose objects keep a path directory of their variant column: its first pass counts each from
    /// its directory alone, the rows' value bytes included, and reads none of their rows; its objects shred what a pass that
    /// read them would have chosen, and hold every row.
    /// </summary>
    [Fact]
    public async Task AFirstPassCountsTheInputsItsDirectoriesTellAndReadsNoneOfTheirRows()
    {
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 2);

        // Objects aimed past a mebibyte, which a job writes with a table of contents and counts the paths of first.
        DatasetOptions options = OptionsOf(session);
        options = options with { Layout = options.Layout! with { ObjectBytes = 4 << 20 } };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, options, Ct);
        for (int a = 0; a < 3; a++)
        {
            // No list, whose elements' class a directory does not keep, and no 32-bit float: a duration's doubles, which the
            // directory marks whole, tell their counts as an integer's do.
            Doc[] rows = [.. Enumerable.Range(a * 20_000, 20_000).Select(i => new Doc(0, $"svc-{random.Next(0, 60):D2}", i, Variant.Object(
                ("route", Variant.From($"/api/r{random.Next(20)}")),
                ("status", Variant.From(200 + (random.Next(4) * 100))),
                ("duration", Variant.From(random.NextDouble() * 100)),
                ("user", Variant.From($"user-{random.Next(500)}")))))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        Assert.Equal(3, results.Sum(static result => result.InputsToldByDirectories));
        Assert.Equal(60_000, await dataset.ScanBuilder().CountAsync(Ct));
    }

    /// <summary>
    /// A consolidation of a large append and three small ones, whose variant holds a list, which no path directory tells: on
    /// four lanes and fourteen its first pass counts the large append in parts of its rows, whole chunks of its variant column
    /// each, side by side, and its objects are those the first pass that counts it whole, on one lane, writes.
    /// </summary>
    [Fact]
    public async Task AFirstPassCountsALargeInputInPartsAndWritesWhatItsWholeCountWrites()
    {
        // Objects aimed past a mebibyte, which a job counts the paths of first, and chunks of 4 KiB, so that the large append's
        // variant column holds dozens of them.
        static DatasetOptions Counted(VortexSession session)
        {
            DatasetOptions options = OptionsOf(session);
            return options with { ChunkBytes = 4 << 10, Layout = options.Layout! with { ObjectBytes = 4 << 20 } };
        }

        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(53);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, Doc.Schema, Counted(VortexSession.Default), Ct);
            foreach (int rows in new[] { 24_000, 2_000, 2_000, 2_000 })
            {
                Doc[] docs = [.. Enumerable.Range(0, rows).Select(i => new Doc(0, $"svc-{random.Next(0, 60):D2}", i, Variant.Object(
                    ("route", Variant.From($"/api/r{random.Next(20)}")),
                    ("status", Variant.From(200 + (random.Next(4) * 100))),
                    ("tags", Variant.Array([.. Enumerable.Range(0, random.Next(3)).Select(t => Variant.From($"tag-{random.Next(9)}"))])))))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(docs, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }

        // Whole on one lane, in parts of a thousand rows or more on four lanes and fourteen.
        List<CompactionResult> jobs = await SameObjectsAtDegreesAsync(
            built,
            [1, 4, 14],
            static session => Counted(session) with { CensusPartRows = session.Options.MaxDegreeOfParallelism == 1 ? long.MaxValue / 4 : 1_024 },
            static async dataset => [.. await dataset.ConsolidateAsync(Ct)]);
        string ran = string.Join(", ", jobs.Select(static job => $"{job.ObjectsIn}->{job.ObjectsOut} parts {job.CensusParts}"));
        Assert.Equal(1, jobs.Count(static job => job.CensusParts.Inputs == 0));
        Assert.True(jobs.Count(static job => job.CensusParts is { Inputs: 1, Parts: >= 4 }) == 2, ran);
    }

    /// <summary>
    /// A consolidation of appends whose keys do not interleave, so that its merge takes long runs of one input: its writer
    /// takes them a block's map at a time and sends the blocks through its line on four lanes, and the objects are those of
    /// one lane.
    /// </summary>
    [Fact]
    public async Task LongRunsGoThroughTheWritersLineABlockAtATime()
    {
        List<long>? first = null;
        foreach (int lanes in new[] { 1, 4 })
        {
            Random random = new Random(53);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            DatasetOptions options = OptionsOf(session) with { Write = new VortexWriteOptions { BlockRows = 256 } };
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, options, Ct);
            for (int a = 0; a < 4; a++)
            {
                // Each append's services in two bands of its own, the bands of the appends one after another: their ranges
                // overlap, so they merge, and the merge takes an input's rows a long run at a time.
                Doc[] rows = [.. Enumerable.Range(a * 30_000, 30_000).Select(i => new Doc(0, $"svc-{(random.Next(2) == 0 ? a : a + 4)}-{random.Next(0, 10)}", i, Attributes(random)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
            long onLine = results.Sum(static result => result.WriterBlocksOnLine);
            Assert.True(lanes == 1 ? onLine == 0 : onLine > 0, $"{lanes} lanes: {onLine} blocks on a writer's line");
            List<long> sizes = [];
            await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                sizes.Add(positioned.Entry.Rows);
            }

            first ??= sizes;
            Assert.Equal(first, sizes);
            Assert.Equal(120_000, await dataset.ScanBuilder().CountAsync(Ct));
        }
    }

    /// <summary>A store whose puts, when <paramref name="late"/>, answer once a millisecond went by.</summary>
    private sealed class LatePuts(IObjectStore inner, bool late) : ForwardingStore(inner)
    {
        public override async ValueTask<PutOutcome> PutAsync(string key, System.IO.Pipelines.PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            if (late)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            return await Inner.PutAsync(key, content, length, condition, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Services named at a kibibyte, so that a chunk of the file holds four blocks of 256 rows and ends within an object: the
    /// writers' lines meet the end of a chunk, emitted once every block of it landed and before the next block ingests, and
    /// the segments are the same bytes at one lane, four and fourteen.
    /// </summary>
    [Fact]
    public async Task AConsolidationWhoseChunksEndWithinItsObjectsWritesTheSameSegmentsAtAnyDegree()
    {
        string padding = new string('x', 1_000);
        List<CompactionResult> jobs = await SameSegmentsAtDegreesAsync(
            [1, 4, 14],
            random => $"svc-{random.Next(0, 60):D2}-{padding}",
            static session => OptionsOf(session) with { Write = new VortexWriteOptions { BlockRows = 256 } });
        Assert.True(jobs.Sum(static job => job.WriterBlocksOnLine) > 0, "no block went through a writer's line");
        Assert.True(jobs.Sum(static job => job.WriterChunksEndedOnLine) > 0, "no chunk ended on a writer's line");
    }

    /// <summary>
    /// One store of six appends of 8 000 rows, whose services <paramref name="service"/> names, copied once a degree of
    /// <paramref name="degrees"/> and consolidated there with <paramref name="optionsOf"/>: every segment of every object is
    /// the same bytes on every copy. The jobs' results, of every degree.
    /// </summary>
    private static async Task<List<CompactionResult>> SameSegmentsAtDegreesAsync(
        int[] degrees, Func<Random, string> service, Func<VortexSession, DatasetOptions> optionsOf)
    {
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(29);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, Doc.Schema, OptionsOf(VortexSession.Default), Ct);
            for (int a = 0; a < 6; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), service(random), i, Attributes(random)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }

        return await SameObjectsAtDegreesAsync(built, degrees, optionsOf, static async dataset => [.. await dataset.ConsolidateAsync(Ct)]);
    }

    /// <summary>A copy of every object of <paramref name="built"/>, under the same keys.</summary>
    private static async Task<MemoryObjectStore> CopyAsync(MemoryObjectStore built)
    {
        MemoryObjectStore copy = new MemoryObjectStore();
        Vorticity.IO.IObjectStore from = built;
        await foreach (Vorticity.IO.ObjectListing listed in from.ListAsync(string.Empty, null, Ct))
        {
            byte[] bytes = new byte[listed.Head.Length];
            await from.ReadAsync(listed.Key, 0, bytes, null, Ct);
            await ((Vorticity.IO.IObjectStore)copy).PutAsync(listed.Key, new System.Buffers.ReadOnlySequence<byte>(bytes), Vorticity.IO.PutCondition.None, Ct);
        }

        return copy;
    }

    /// <summary>
    /// The store <paramref name="built"/>, copied once a degree of <paramref name="degrees"/>, its jobs run there by
    /// <paramref name="drive"/> under <paramref name="optionsOf"/>: every segment of every object is the same bytes on every
    /// copy. The jobs' results, of every degree.
    /// </summary>
    private static async Task<List<CompactionResult>> SameObjectsAtDegreesAsync(
        MemoryObjectStore built, int[] degrees, Func<VortexSession, DatasetOptions> optionsOf, Func<VortexDataset, Task<List<CompactionResult>>> drive)
    {
        List<List<(string Path, byte[] Bytes)>> runs = [];
        List<CompactionResult> jobs = [];

        // A copy keeps the objects' identities, which order the objects of equal keys in the tree, as a maintainer's store does.
        for (int run = 0; run < degrees.Length; run++)
        {
            await using MemoryObjectStore copy = await CopyAsync(built);
            int degree = degrees[run];
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(copy, optionsOf(session), Ct);
            jobs.AddRange(await drive(dataset));
            List<(string Path, byte[] Bytes)> segments = [];
            int index = 0;
            await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                byte[] bytes = new byte[positioned.Entry.Bytes];
                await ((Vorticity.IO.IObjectStore)copy).ReadAsync(positioned.Entry.Key, 0, bytes, null, Ct);
                await using VortexFile file = await VortexSession.Default.OpenAsync(new Vorticity.IO.MemorySegmentSource(bytes), cancellationToken: Ct);
                Collect(await file.GetLayoutAsync(Ct), $"object {index}", bytes, file, segments);
                index++;
            }

            runs.Add(segments);
        }

        Assert.True(runs[0].Count >= 8, $"{runs[0].Count} segments");
        System.Text.StringBuilder differ = new System.Text.StringBuilder();
        for (int run = 1; run < degrees.Length; run++)
        {
            Assert.Equal(runs[0].Count, runs[run].Count);
            for (int i = 0; i < runs[0].Count; i++)
            {
                if (runs[0][i].Path != runs[run][i].Path || !runs[0][i].Bytes.AsSpan().SequenceEqual(runs[run][i].Bytes))
                {
                    differ.Append("degree ").Append(degrees[run]).Append(", ").Append(runs[run][i].Path).Append(": ")
                        .Append(runs[0][i].Bytes.Length).Append(" bytes against ").Append(runs[run][i].Bytes.Length).Append('\n');
                }
            }
        }

        Assert.True(differ.Length == 0, "segments differ:\n" + differ);
        return jobs;
    }

    private static async Task<(List<long> Objects, List<Doc> Rows, long Flown)> ConsolidateAsync(int lanes, int held = 0)
    {
        Random random = new Random(29);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        long[] taken = new long[held];
        for (int i = 0; i < held; i++)
        {
            Assert.True(session.Lanes.TryEnter(out taken[i]));
        }
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, OptionsOf(session), Ct);
        for (int a = 0; a < 6; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        Assert.NotEmpty(results);
        List<long> objects = [];
        await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects.Add(positioned.Entry.Rows);
        }

        List<Doc> read = [.. (await dataset.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence)];
        foreach (long entered in taken)
        {
            session.Lanes.Exit(entered);
        }

        return (objects, read, results.Sum(static result => result.WriterBatchesFlown));
    }

    /// <summary>The dataset of two days of documents, a service's rows together, in objects of 256 KiB and blocks of 1 024 rows.</summary>
    private static DatasetOptions OptionsOf(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Write = new VortexWriteOptions { BlockRows = 1_024 },
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Doc.Day)),
            SortKey = [LayoutValue.Column(nameof(Doc.Service))],
            ObjectBytes = 1 << 18,
            CalmAfter = null,
        },
    };

    private static void Collect(VortexLayout node, string path, byte[] bytes, VortexFile file, List<(string Path, byte[] Bytes)> into)
    {
        for (int s = 0; s < node.Segments.Length; s++)
        {
            VortexSegment segment = file.SegmentMap[node.Segments[s]];
            into.Add(($"{path}/{node.Encoding}/{node.ArrayEncoding}#{s}", bytes.AsSpan((int)segment.Offset, (int)segment.Length).ToArray()));
        }

        for (int c = 0; c < node.Children.Length; c++)
        {
            Collect(node.Children[c], $"{path}.{c}", bytes, file, into);
        }
    }

    /// <summary>Attributes of a service's records: a route, a status, a duration, a user, and now and then a key of its own.</summary>
    private static Variant Attributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(20)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(500)}")),
        ];
        if (random.Next(5) == 0)
        {
            fields.Add(($"k{random.Next(40)}", Variant.From(random.Next(1_000))));
        }

        return Variant.Object([.. fields]);
    }

    [VortexRecord]
    public partial record struct Doc(int Day, string Service, long Sequence, Variant Attributes);

    [VortexRecord]
    public partial record struct LogDoc(int Day, string Service, string? Message, long At, Variant Attributes);
}
