// The dataset's compaction, held to two acceptances: the levels' invariant after every compaction
// step of a randomised append stream, and a compaction reading level 0 through its runs producing
// the same object as one reading inputs sorted beforehand.
//
// THE DATA IS THE ONE THAT BREAKS A LAZY MERGE, deliberately the same shape as the clustering
// tests': the objects' key ranges INTERLEAVE (object i holds the keys congruent to i modulo four),
// they are appended OUT OF ORDER, and the keys inside one object are SHUFFLED. A compaction that
// concatenated its inputs instead of merging them would be caught on the second key; one that read
// its inputs in file order instead of through their runs would be caught on the first.
//
// AND THE ORACLE IS ALWAYS THE SAME ROWS WRITTEN ANOTHER WAY, never a number typed into the test: a
// dataset compacted from four interleaved objects is compared against one file written from the
// same rows already sorted, which is the second acceptance read literally.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Diagnostics;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetCompactionTests
{
    private const int Objects = 4;
    private const int PerObject = 250;
    private const int Rows = Objects * PerObject;

    [Fact]
    public async Task ACompactionOfLevelZeroProducesTheObjectASortedInputWouldHave()
    {
        // A compaction reading level 0 through its runs produces the same object as one reading
        // inputs sorted beforehand.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        CompactionOptions options = Options(target: 1 << 20);
        CompactionPlan plan = await dataset.PlanCompactionAsync(options);
        Assert.Equal(CompactionTrigger.LevelZeroCeiling, plan.Job!.Trigger);
        Assert.Equal(CompactionStyle.Leveled, plan.Style);
        Assert.Equal(Objects, plan.Job.Inputs.Count);
        Assert.Equal(Rows, plan.Job.Rows);

        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options));
        Assert.Equal(OperationOutcome.Applied, result.Outcome);
        Assert.Equal(Objects, result.ObjectsIn);
        Assert.Equal(1, result.ObjectsOut);
        Assert.Equal(Rows, result.Rows);
        Assert.Equal(0, dataset.Levels[0].Entries);
        Assert.Equal(1, dataset.Levels[1].Entries);
        Assert.Equal(Rows, dataset.RowCount);

        // The oracle: the same rows, sorted before they were written, in one file.
        byte[] sorted = await SortedFileAsync(types, schema);
        await using MemorySegmentSource source = new MemorySegmentSource(sorted);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);

        List<(long Key, double Measure)> expected = await RowsAsync(file.Scan());
        List<(long Key, double Measure)> produced = await RowsAsync(dataset.Scan());
        Assert.Equal(expected, produced);

        // And the output says what it is: a sorted key column, which is what lets a lookup inside it
        // be a seek without reading the run at all.
        ObjectEntry compacted = Assert.Single(await ObjectsAsync(dataset));
        await using ObjectSegmentSource bytes = new ObjectSegmentSource(store, compacted.Key);
        await using VortexFile output = await VortexFile.OpenAsync(bytes, new VortexOpenOptions(), default);
        Assert.True(IsSorted(output, "key"), "a merge writes its output sorted by construction (§5.3)");

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET COMPACTION: {result.ObjectsIn} interleaved objects of level 0 merged into {result.ObjectsOut} sorted object at level 1, {result.Rows} rows, {result.BytesIn} bytes read and {result.BytesOut} written.\n"));
    }

    [Fact]
    public async Task TheInvariantHoldsAfterEveryStepOfARandomisedAppendStream()
    {
        // The stream appends a random number of rows at a random offset, and compaction is run to
        // exhaustion at random moments — so level 0 is sometimes over its ceiling and sometimes
        // empty, and the invariant is checked after every single step.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());

        // A small target so the merges roll into several objects per level, a fan-out of three so
        // level 1 goes over its size and the second trigger fires too, and a ceiling of two so
        // level 0 is over its bound most of the time.
        CompactionOptions options = Options(target: 3 << 10) with { Fanout = 3 };
        Random random = new Random(0x5A1AD);
        List<long> appended = [];
        int compactions = 0;
        for (int round = 0; round < 24; round++)
        {
            int count = 40 + random.Next(120);
            long from = random.Next(2_000);
            await dataset.AppendAsync(Shuffled(types, schema, from, count, random.Next()));
            for (int i = 0; i < count; i++)
            {
                appended.Add(from + i);
            }

            if (random.Next(2) != 0)
            {
                continue;
            }

            while (await dataset.CompactAsync(options) is { } step)
            {
                compactions++;
                Assert.Equal(OperationOutcome.Applied, step.Outcome);
                await AssertInvariantAsync(dataset, options, appended);
            }
        }

        while (await dataset.CompactAsync(options) is { } last)
        {
            compactions++;
            Assert.Equal(OperationOutcome.Applied, last.Outcome);
            await AssertInvariantAsync(dataset, options, appended);
        }

        // Drained: level 0's ceiling holds, and the lag it would have reported is gone.
        Assert.True(dataset.Levels[0].Entries <= options.LevelZeroCeiling);
        Assert.Equal(0, dataset.Lag);
        Assert.True(compactions >= 10, $"the stream must exercise compaction; it ran {compactions} time(s)");
        Assert.True(dataset.Levels.Count >= 3, "the second trigger must have reached a third level");

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET COMPACTION STREAM: {compactions} compaction(s) over {appended.Count} appended rows left {dataset.ObjectCount} object(s) across {dataset.Levels.Count} level(s), lag 0.\n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheWriteAmplificationIsMeasuredOverAStreamThatReachesSeveralLevels(bool leveled)
    {
        // The price of each style: leveled rewrites a row about F/2 times per level it crosses,
        // tiered about once. Measured in ROWS, which is what the sentence counts: the rows every
        // compaction rewrote, over the rows appended, with compaction drained after every append —
        // the steady state of a dataset whose compactor keeps up.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, leveled ? Clustered() : Unclustered());

        const int fanout = 4;
        CompactionOptions options = Options(target: 2 << 10) with { Fanout = fanout };
        Random random = new Random(0xA3F1);
        long appended = 0;
        long rewritten = 0;
        long bytesIn = 0;
        long bytesOut = 0;
        for (int round = 0; round < 64; round++)
        {
            int count = 50 + random.Next(100);
            await dataset.AppendAsync(Shuffled(types, schema, random.Next(1_000_000), count, random.Next()));
            appended += count;
            while (await dataset.CompactAsync(options) is { } step)
            {
                Assert.Equal(OperationOutcome.Applied, step.Outcome);
                rewritten += step.Rows;
                bytesIn += step.BytesIn;
                bytesOut += step.BytesOut;
            }
        }

        int levels = dataset.Levels.Count;
        double amplification = (double)rewritten / appended;
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET WRITE AMPLIFICATION ({(leveled ? "leveled" : "tiered")}, F = {fanout}): {rewritten} rows rewritten for {appended} appended, {amplification:F2}x over {levels} level(s); {bytesIn} bytes read, {bytesOut} written.\n"));

        Assert.True(levels >= 3, $"the stream must cross at least two levels; it reached {levels}");
        Assert.Equal(appended, dataset.RowCount);
        Assert.True(amplification >= 1.0, "every row but level 0's few has been rewritten at least once");

        // The bounds are the design's, not chosen: a tiered crossing rewrites a row exactly once,
        // so no row is rewritten more often than there are levels above level 0; a leveled crossing
        // rewrites the level's overlapping objects, F/2 on average (the spec's number) and at most
        // F + 1 times the rows that arrived.
        int crossings = levels - 1;
        Assert.True(
            leveled ? amplification <= (fanout + 1) * crossings : amplification <= crossings,
            $"{amplification:F2}x over {crossings} crossing(s) exceeds what the policy can rewrite");
    }

    [Fact]
    public async Task ASeekOpensLevelZeroAndOneObjectPerLevelAbove()
    {
        // The key cursor is held to the bound `InKeyOrder` is held to. A multi-level dataset from
        // a randomised stream; a fresh cursor per sought key, whose seek must open no more than
        // level 0's objects and one per level above it, and whose walk from there must be every
        // key at or after it, in order.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        List<long> appended = await LevelledAsync(dataset, types, schema);
        appended.Sort();

        int levels = 0;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            levels += dataset.Levels[level].Entries > 0 ? 1 : 0;
        }

        Assert.True(levels >= 2, $"the stream must fill at least two levels above 0; it filled {levels}");
        Assert.True(dataset.ObjectCount > dataset.Levels[0].Entries + levels, "some level must hold several objects");
        long bound = dataset.Levels[0].Entries + levels;
        foreach (long sought in (long[])[-5, 0, 333, 1_000, 1_777, 2_150])
        {
            await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset);
            bool found = await cursor.SeekAsync(FilterLiteral.From(sought));
            Assert.True(cursor.Cursors <= bound, $"a seek to {sought} opened {cursor.Cursors} cursors against {bound}");

            List<long> walked = [];
            for (bool any = found; any; any = await cursor.NextAsync())
            {
                walked.Add(cursor.Key.SignedValue);
            }

            Assert.Equal(appended.FindAll(key => key >= sought), walked);
        }
    }

    [Fact]
    public async Task ACountOnTheKeyOpensOnlyTheObjectsTheRangeCuts()
    {
        // A count over a key range: the objects wholly inside the range are counted from their
        // entries; only the ones the range cuts are opened -- at most two per level above 0, plus
        // level 0's. The oracle is the keys appended.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        List<long> appended = await LevelledAsync(dataset, types, schema);
        int levels = 0;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            levels += dataset.Levels[level].Entries > 0 ? 1 : 0;
        }

        long bound = dataset.Levels[0].Entries + (2 * levels);
        long counted = 0;
        foreach ((long low, long high) in ((long, long)[])[(100, 1_900), (0, 2_200), (777, 778), (1_500, 1_200)])
        {
            DatasetScanMetrics metrics = new DatasetScanMetrics();
            VortexExpr range = Expr.And(
                Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(low))),
                Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(high))));
            long count = await dataset.Scan().Where(range).WithMetrics(metrics).CountAsync();
            Assert.Equal(appended.FindAll(key => key >= low && key < high).Count, count);
            Assert.True(metrics.ObjectsOpened <= bound, $"[{low}, {high}) opened {metrics.ObjectsOpened} objects against {bound}");
            counted += metrics.ObjectsCounted;
        }

        Assert.True(counted > 0, "some object must lie wholly inside a range and be counted unopened");

        // The rank is the count below the key, by the same path.
        foreach (long key in (long[])[-1, 0, 1_000, 5_000])
        {
            Assert.Equal(appended.FindAll(k => k < key).Count, await dataset.RankAsync(FilterLiteral.From(key)));
        }

        // A filter the key's summaries cannot count opens what the summaries keep, and answers the same.
        DatasetScanMetrics other = new DatasetScanMetrics();
        VortexExpr measure = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(100.0)));
        Assert.Equal(appended.FindAll(key => key / 4.0 < 100.0).Count, await dataset.Scan().Where(measure).WithMetrics(other).CountAsync());
        Assert.Equal(0, other.ObjectsCounted);
    }

    [Fact]
    public async Task OutputsAreRolledAtTheDestinationSizeAndStayKeyDisjoint()
    {
        // A compaction writes one or more outputs at the target size of the destination level,
        // whose objects must stay key-disjoint. A target below one object's size forces both.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        // Big enough a fan-out that level 1 is not immediately over its own size, small enough a
        // target that the merge has to roll.
        CompactionOptions options = Options(target: 2 << 10) with { Fanout = 1_000 };
        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options));
        Assert.True(result.ObjectsOut > 1, $"a target of 2 KiB must roll; it wrote {result.ObjectsOut}");
        Assert.Equal(Rows, result.Rows);
        Assert.Equal(Rows, dataset.RowCount);

        List<(long Min, long Max)> ranges = await RangesAsync(dataset, level: 1);
        Assert.Equal(result.ObjectsOut, ranges.Count);
        for (int i = 1; i < ranges.Count; i++)
        {
            Assert.True(
                ranges[i - 1].Max < ranges[i].Min,
                $"object {i - 1} ends at {ranges[i - 1].Max} and object {i} starts at {ranges[i].Min}");
        }

        // And the rows are still every key, once, in order.
        Assert.Equal(await SortedKeysAsync(), await KeysAsync(dataset.Scan()));

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET COMPACTION ROLL: a target of 2 KiB split {result.Rows} rows into {result.ObjectsOut} key-disjoint objects at level 1.\n"));
    }

    [Fact]
    public async Task ATieredCompactionConcatenatesAndKeepsTheDatasetsRowOrder()
    {
        // The default style is leveled when a clustering key is declared and tiered otherwise. A
        // tiered compaction is a concatenation, and without a clustering key the dataset's order is
        // the first row position — so the rows must come out in exactly the same sequence.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Unclustered());
        for (int i = 0; i < 6; i++)
        {
            await dataset.AppendAsync(Shuffled(types, schema, i * 100, 100, seed: i + 1));
        }

        List<long> before = await KeysAsync(dataset.Scan());
        CompactionOptions options = Options(target: 1 << 20) with { LevelZeroCeiling = 3 };
        CompactionPlan plan = await dataset.PlanCompactionAsync(options);
        Assert.Equal(CompactionStyle.Tiered, plan.Style);
        Assert.False(plan.IsClustered);

        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options));
        Assert.Equal(6, result.ObjectsIn);
        Assert.Equal(1, result.ObjectsOut);
        Assert.Equal(600, dataset.RowCount);

        // The same rows in the same sequence, which is what a concatenation promises and a merge
        // does not: these keys are shuffled inside each object and still come back shuffled.
        Assert.Equal(before, await KeysAsync(dataset.Scan()));
        Assert.Equal(before, await KeysAsync(dataset.Rows(0, 600)));
        Assert.Equal(before.GetRange(150, 300), await KeysAsync(dataset.Rows(150, 450)));
    }

    [Fact]
    public async Task ALevelAboveItsSizeIsCompactedIntoTheOneAbove()
    {
        // The level-size trigger. Level 0 goes up first; then level 1 is over the size its fan-out
        // allows, and the next step moves it to level 2 — the lowest level over its size first.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        // A target of 2 KiB and a fan-out of 2: level 1 holds at most 4 KiB, and the rolled outputs
        // are well past it.
        CompactionOptions options = Options(target: 2 << 10) with { Fanout = 2 };
        Assert.Equal(CompactionTrigger.LevelZeroCeiling, (await dataset.PlanCompactionAsync(options)).Job!.Trigger);
        _ = await dataset.CompactAsync(options);

        CompactionPlan second = await dataset.PlanCompactionAsync(options);
        Assert.Equal(CompactionTrigger.LevelSize, second.Job!.Trigger);
        Assert.Equal(1, second.Job.FromLevel);
        Assert.Equal(2, second.Job.ToLevel);

        CompactionResult moved = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options));
        Assert.Equal(1, moved.FromLevel);
        Assert.Equal(2, moved.ToLevel);
        Assert.True(dataset.Levels.Count >= 3);
        Assert.Equal(Rows, dataset.RowCount);
        Assert.Equal(await SortedKeysAsync(), await KeysAsync(dataset.Scan()));
    }

    [Fact]
    public async Task TheTopLevelOfACappedDatasetHasNoSize()
    {
        // The header's `Levels`: the same data as above, where level 1 went over its size
        // and moved up. Capped at two levels, level 1 is the top: it only grows, and a drain ends.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, Clustered() with { Compaction = new CompactionSettings(2, 0, 0) });
        Assert.Equal(2, dataset.Compaction.Levels);
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        CompactionOptions options = Options(target: 2 << 10) with { Fanout = 2 };
        Assert.Equal(CompactionTrigger.LevelZeroCeiling, (await dataset.PlanCompactionAsync(options)).Job!.Trigger);
        _ = await dataset.CompactAsync(options);
        Assert.True(dataset.Levels[1].Entries > 1);

        Assert.Null((await dataset.PlanCompactionAsync(options)).Job);
        Assert.Equal(2, dataset.Levels.Count);
        Assert.Equal(await SortedKeysAsync(), await KeysAsync(dataset.Scan()));

        // And a cap that leaves level 0 nowhere to go is refused where it is stated.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionOptions { MaxLevels = 1 });
    }

    [Fact]
    public async Task APlanReadsEntriesAndChangesNothing()
    {
        // Compaction is the user's background job, so planning must be pure: a caller
        // reads what it would cost and decides. The version does not move and no object is written.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        ulong version = dataset.Version;
        long objects = store.Count;
        CompactionPlan plan = await dataset.PlanCompactionAsync(Options(target: 1 << 20));

        Assert.Equal(version, dataset.Version);
        Assert.Equal(objects, store.Count);
        Assert.Equal(version, plan.Version);
        Assert.True(plan.HasWork);
        Assert.Equal([(long)Objects], plan.ObjectsByLevel);
        Assert.Equal(plan.Job!.Bytes, plan.BytesByLevel[0]);
        Assert.Equal(0, plan.FragmentedObjects);

        // Under the specification's own ceiling of eight, four objects are not due at all.
        CompactionPlan idle = await dataset.PlanCompactionAsync();
        Assert.False(idle.HasWork);
        Assert.Null(idle.Job);
        Assert.Equal(0, idle.Lag);
    }

    [Fact]
    public async Task ACompositeClusteringKeyIsMergedThroughItsRun()
    {
        // A composite key's objects are read by `InKeyOrder(paths)` over the mandatory composite
        // run and merged on the tuple. Four interleaved, shuffled objects; the output is one
        // object in tuple order, and the dataset reads back in that order.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions composite = Unclustered() with { ClusteringKey = ["key", "measure"] };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, composite);
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        CompactionOptions options = Options(target: 1 << 20);
        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options));
        Assert.Equal(OperationOutcome.Applied, result.Outcome);
        Assert.Equal((Objects, 1L, (long)Rows), (result.ObjectsIn, result.ObjectsOut, result.Rows));
        Assert.Equal(CompactionStyle.Leveled, result.Style);

        // The one output, read in its own file order, is the tuple order; and the dataset's
        // key-ordered read on the tuple, in both directions, is the same rows.
        Assert.Equal(await SortedKeysAsync(), await KeysAsync(dataset.Scan()));
        Assert.Equal(await SortedKeysAsync(), await KeysAsync(dataset.Scan().InKeyOrder(["key", "measure"])));
        List<long> descending = await SortedKeysAsync();
        descending.Reverse();
        Assert.Equal(descending, await KeysAsync(dataset.Scan().InKeyOrder(["key", "measure"], descending: true)));
    }

    [Fact]
    public async Task ACompositeKeyHoldingANullIsRefusedByName()
    {
        // What stays refused: a composite run holds no tuple with a null, so a merge through it
        // would drop the row. One column's null keys are read last and merged (the test below,
        // `TheNullKeysOfOneColumnAreMergedLast`); a tuple's are not.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.Nullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, Unclustered() with { ClusteringKey = ["key", "measure"] });
        await dataset.AppendAsync(NullableMeasures(types, schema, 0, nullEvery: 5));
        await dataset.AppendAsync(NullableMeasures(types, schema, 1, nullEvery: 5));

        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await dataset.CompactAsync(Options(target: 1 << 20) with { LevelZeroCeiling = 1 }));
        Assert.Contains("12 §4.6", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'measure'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, dataset.Levels[0].Entries);
    }

    [Fact]
    public async Task TheNullKeysOfOneColumnAreMergedLast()
    {
        // A key column holding nulls compacts. The core's key-ordered read delivers them last,
        // which is where the merge's row encoding sorts them, so no row is dropped and the output
        // reads back keyed rows first, nulls after.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["measure", "key"],
            [types.Primitive(PType.F64, Nullability.Nullable), types.Primitive(PType.I64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, Unclustered() with { ClusteringKey = ["measure"] });
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(NullableMeasures(types, schema, residue, nullEvery: 7, measureFirst: true));
        }

        // Before any compaction, the read merge across the four objects puts them in the same place,
        // in both directions: the merge encodes nulls last because the core delivers them last. The
        // row encoding's own default puts them first, and a null head would then go out before
        // another object's largest keys.
        foreach (bool descending in (bool[])[false, true])
        {
            List<double?> merged = await MeasuresAsync(dataset.Scan().InKeyOrder("measure", descending));
            int tail = merged.Count(m => m is null);
            Assert.Equal(Rows, merged.Count);
            Assert.All(merged.Skip(merged.Count - tail), m => Assert.Null(m));
            List<double?> keys = [.. merged.Take(merged.Count - tail)];
            Assert.Equal(descending ? keys.OrderDescending() : keys.Order(), keys);
        }

        CompactionResult result = Assert.IsType<CompactionResult>(
            await dataset.CompactAsync(Options(target: 1 << 20)));
        Assert.Equal((long)Rows, result.Rows);

        List<double?> measures = await MeasuresAsync(dataset.Scan());
        int nulls = measures.Count(m => m is null);
        Assert.True(nulls > 0);
        Assert.All(measures.Take(measures.Count - nulls), m => Assert.NotNull(m));
        Assert.All(measures.Skip(measures.Count - nulls), m => Assert.Null(m));
        List<double?> keyed = [.. measures.Take(measures.Count - nulls)];
        Assert.Equal(keyed.Order(), keyed);
    }

    /// <summary>
    /// A randomised append stream drained into several levels of several objects each; the keys
    /// appended, as the oracle.
    /// </summary>
    private static async Task<List<long>> LevelledAsync(VortexDataset dataset, DTypeArena types, DType schema)
    {
        CompactionOptions options = Options(target: 3 << 10) with { Fanout = 3 };
        Random random = new Random(0x5A1AD);
        List<long> appended = [];
        for (int round = 0; round < 24; round++)
        {
            int count = 40 + random.Next(120);
            long from = random.Next(2_000);
            await dataset.AppendAsync(Shuffled(types, schema, from, count, random.Next()));
            for (int i = 0; i < count; i++)
            {
                appended.Add(from + i);
            }

            if (random.Next(2) == 0)
            {
                while (await dataset.CompactAsync(options) is not null)
                {
                }
            }
        }

        return appended;
    }

    /// <summary>The first column of every batch, a nullable f64, in delivery order.</summary>
    private static async Task<List<double?>> MeasuresAsync(DatasetScanBuilder scan)
    {
        List<double?> measures = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            VortexColumn column = batch.Column(0);
            ReadOnlySpan<double> values = column.AsPrimitive<double>().Values;
            for (int i = 0; i < batch.RowCount; i++)
            {
                measures.Add(column.IsValid(i) ? values[i] : null);
            }
        }

        return measures;
    }

    /// <summary>
    /// One object's rows with a nullable f64: the keys congruent to <paramref name="residue"/> mod
    /// four, every <paramref name="nullEvery"/>-th measure null.
    /// </summary>
    private static async IAsyncEnumerable<RecordBatch> NullableMeasures(
        DTypeArena types, DType schema, int residue, int nullEvery, bool measureFirst = false)
    {
        int count = PerObject;
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measureBuffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        VortexBuffer bits = arena.Allocate((count + 7) / 8, 8, out Span<byte> valid);
        valid.Clear();
        Span<long> keys = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measures = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < count; row++)
        {
            long key = ((long)row * Objects) + residue;
            keys[row] = key;
            measures[row] = ((key * 37) % 101) / 4.0;
            if (key % nullEvery != 0)
            {
                valid[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.Nullable);
        int validity = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
        int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
        int measureNode = arena.AddPrimitive(f64, count, Validity.Bitmap(validity), PType.F64, measureBuffer);
        int root = arena.AddStruct(
            schema, count, Validity.NonNullable, measureFirst ? [measureNode, keyNode] : [keyNode, measureNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        yield return batch;
        await Task.CompletedTask;
    }

    /// <summary>
    /// The invariant the levels keep, asked of a version rather than assumed of it: every level
    /// above 0 key-disjoint, level 0 inside its ceiling, and every appended row held.
    /// </summary>
    private static async Task AssertInvariantAsync(
        VortexDataset dataset, CompactionOptions options, List<long> appended)
    {
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            List<(long Min, long Max)> ranges = await RangesAsync(dataset, level);
            for (int i = 1; i < ranges.Count; i++)
            {
                Assert.True(
                    ranges[i - 1].Max < ranges[i].Min,
                    $"level {level}: [{ranges[i - 1].Min}, {ranges[i - 1].Max}] then [{ranges[i].Min}, {ranges[i].Max}]");
            }
        }

        // The ceiling trigger is the first one checked, so the step that follows a level-0 overflow
        // is the one that empties it: after any step at all, level 0 is inside its bound.
        Assert.True(dataset.Levels[0].Entries <= options.LevelZeroCeiling);
        Assert.Equal(appended.Count, dataset.RowCount);

        List<long> sorted = [.. appended];
        sorted.Sort();
        List<long> held = await KeysAsync(dataset.Scan());
        held.Sort();
        Assert.Equal(sorted, held);
    }

    /// <summary>The key range of every object of one level, in the tree's order.</summary>
    private static async Task<List<(long Min, long Max)>> RangesAsync(VortexDataset dataset, int level)
    {
        List<(long Min, long Max)> ranges = [];
        await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, default))
        {
            ObjectEntry held = ObjectEntry.FromBytes(entry.Value.Span);
            Assert.True(held.Summaries.TryGet("key", out ColumnSummary key));
            ranges.Add((key.Min.SignedValue, key.Max.SignedValue));
        }

        return ranges;
    }

    private static bool IsSorted(VortexFile file, string path)
    {
        int index = file.Schema.IndexOfField(path);
        return index >= 0
            && file.HasFileStatistics
            && file.Statistics.GetField(index).TryGetIsSorted(out bool sorted)
            && sorted;
    }

    private static async Task<List<ObjectEntry>> ObjectsAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (ObjectEntry entry in dataset.ObjectsAsync())
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static Task<List<long>> SortedKeysAsync()
    {
        List<long> keys = [];
        for (long key = 0; key < Rows; key++)
        {
            keys.Add(key);
        }

        return Task.FromResult(keys);
    }

    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async Task<List<(long Key, double Measure)>> RowsAsync(DatasetScanBuilder scan)
    {
        List<(long Key, double Measure)> rows = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
            ReadOnlySpan<double> measures = batch.Column(1).AsPrimitive<double>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                rows.Add((keys[row], measures[row]));
            }
        }

        return rows;
    }

    private static async Task<List<(long Key, double Measure)>> RowsAsync(ScanBuilder scan)
    {
        List<(long Key, double Measure)> rows = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
            ReadOnlySpan<double> measures = batch.Column(1).AsPrimitive<double>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                rows.Add((keys[row], measures[row]));
            }
        }

        return rows;
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Clustered() => Unclustered() with { ClusteringKey = ["key"] };

    private static DatasetOptions Unclustered() => new DatasetOptions
    {
        Seed = 0xC0A9AC7_5EED,

        // Small blocks, so that the sink's position moves while the object is being written and a
        // roll at the destination's target size is something the merge can actually see.
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    private static CompactionOptions Options(long target) => new CompactionOptions
    {
        LevelZeroCeiling = 2,
        TargetBytesAtLevelOne = target,
        MaxObjectBytes = 1L << 30,
    };

    /// <summary>The same rows as the four objects, sorted before a byte of them is written.</summary>
    private static async Task<byte[]> SortedFileAsync(DTypeArena types, DType schema)
    {
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, Clustered().Write))
        {
            await foreach (RecordBatch batch in Ordered(types, schema, 0, Rows))
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    /// <summary>One object's rows: the keys congruent to <paramref name="residue"/> mod four, shuffled.</summary>
    private static IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int residue)
    {
        long[] keys = new long[PerObject];
        for (int i = 0; i < PerObject; i++)
        {
            keys[i] = ((long)i * Objects) + residue;
        }

        Shuffle(keys, residue + 1);
        return Of(types, schema, keys);
    }

    /// <summary><paramref name="count"/> consecutive keys from <paramref name="from"/>, shuffled.</summary>
    private static IAsyncEnumerable<RecordBatch> Shuffled(
        DTypeArena types, DType schema, long from, int count, int seed)
    {
        long[] keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = from + i;
        }

        Shuffle(keys, seed);
        return Of(types, schema, keys);
    }

    /// <summary><paramref name="count"/> consecutive keys from <paramref name="from"/>, in order.</summary>
    private static IAsyncEnumerable<RecordBatch> Ordered(DTypeArena types, DType schema, long from, int count)
    {
        long[] keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = from + i;
        }

        return Of(types, schema, keys);
    }

    /// <summary>A fixed shuffle: the same every run, and nothing like the file order.</summary>
    private static void Shuffle(long[] keys, int seed)
    {
        Random random = new Random(seed);
        for (int i = keys.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }
    }

    private static async IAsyncEnumerable<RecordBatch> Of(DTypeArena types, DType schema, long[] keys)
    {
        const int size = 125;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        for (int start = 0; start < keys.Length; start += size)
        {
            int count = Math.Min(size, keys.Length - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = keys[start + row];
                measureValues[row] = keys[start + row] / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }
}
