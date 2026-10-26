// Rows that come a little late, behind the rows the levels past 0 already hold: an exit of level 0 keeps them apart there,
// an object a clustering value, rather than read and write again the objects of its destination they fall among. Level 0
// keeps within its ceiling, the late sharing it with the rest, and every question finds them as the rows say.
//
// THE ORACLE IS THE ROWS AND THE JOBS' INPUTS: the rows read back are the rows appended, the exits of level 0 read fewer
// rows of the level below than were appended, where they read its last object at almost every exit, level 0 keeps within
// its ceiling, and what waits there late lies within one value.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLateRowsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The ids of one clustering value, so that the objects of level 1 grow past what an exit writes.</summary>
    private const long Value = 4_000;

    [Fact]
    public async Task ExitsLeaveTheLevelBelowAloneForRowsALittleLate()
    {
        Stream late = await StreamAsync(latePercent: 2);
        Stream ordered = await StreamAsync(latePercent: 0);

        Console.Out.Write(FormattableString.Invariant(
            $"LATE ROWS: {late.Appended} rows, 2 % a little late: the exits read {late.Met} rows of the level below, {late.Rewritten} rows rewritten ({(double)late.Rewritten / late.Appended:F2}x), {late.Waiting} objects late in level 0; in order: {ordered.Met} and {ordered.Rewritten} ({(double)ordered.Rewritten / ordered.Appended:F2}x).\n"));
        Assert.Equal(0, ordered.Met);
        Assert.Equal(0, ordered.Waiting);

        // Merged in at every exit, the late rows made the exits read twice the rows appended of the level below, the last
        // object of each value growing with them, and the jobs rewrite 3.0 times each row.
        Assert.True(late.Met <= late.Appended, $"the exits read {late.Met} rows of the level below for {late.Appended} appended");
        Assert.True(late.Rewritten <= 2 * ordered.Rewritten, $"{late.Rewritten} rows rewritten with late rows, {ordered.Rewritten} in order");
    }

    /// <summary>
    /// Appends of 100 ids, compaction drained after each, then the rows read back and every object checked: the rows
    /// appended and rewritten, the rows of the level below the exits of level 0 read, and the late objects level 0 still
    /// holds. The ids follow each other, but for <paramref name="latePercent"/> of each append, 20 to 200 ids behind the last.
    /// </summary>
    private static async Task<Stream> StreamAsync(int latePercent)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Numbered.Schema, new DatasetOptions
        {
            Seed = 0x1A7E_0001,
            ClusteringKey = [nameof(Numbered.Id)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Bucket(Value) },
        }, Ct);
        CompactionOptions compaction = new CompactionOptions();

        Random random = new Random(0x1A7E);
        List<long> ids = [];
        long next = 0;
        long rewritten = 0;
        long met = 0;
        for (int round = 0; round < 96; round++)
        {
            Numbered[] rows = new Numbered[100];
            for (int i = 0; i < rows.Length; i++)
            {
                long id = next > 200 && random.Next(100) < latePercent ? next - random.Next(20, 200) : next++;
                rows[i] = new Numbered(id, i);
                ids.Add(id);
            }

            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Numbered>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            // The rows an exit wrote past those of level 0 it took are the rows of the level below it read.
            for (int step = 0; ; step++)
            {
                Assert.True(step < 10_000, "compaction never runs out of jobs");
                CompactionPlan plan = await dataset.PlanCompactionAsync(compaction, Ct);
                if (await dataset.CompactAsync(compaction, Ct) is not { } done)
                {
                    break;
                }

                Assert.Equal(OperationOutcome.Applied, done.Outcome);
                rewritten += done.Rows;
                if (done.Trigger == CompactionTrigger.LevelZeroCeiling)
                {
                    met += done.Rows - plan.Job!.Inputs.Where(input => input.Level == 0).Sum(input => input.Entry.Rows);
                }
            }

            if (round % 16 == 15)
            {
                Assert.Equal(ids.Order(), await dataset.Scan<Numbered>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Ct));
            }
        }

        Assert.Equal(ids.Count, dataset.RowCount);
        Assert.Equal(ids.Order(), await dataset.Scan<Numbered>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Ct));
        Assert.Equal(ids.Count(id => id >= 4_500 && id < 5_500), await dataset.Scan<Numbered>().Where(r => r.Id >= 4_500 && r.Id < 5_500).CountAsync(Ct));

        long greatest = long.MinValue;
        List<(long Min, long Max)> level0 = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            Assert.True(held.Entry.Summaries.TryGet(nameof(Numbered.Id), out ColumnSummary column) && column.HasMin && column.HasMax);
            if (held.Level == 0)
            {
                level0.Add((column.Min.SignedValue, column.Max.SignedValue));
            }
            else
            {
                greatest = Math.Max(greatest, column.Max.SignedValue);
            }
        }

        // Level 0 keeps within its ceiling, which the late share with the rest; an object there behind what the levels past
        // it hold came late, and lies within one value.
        Assert.True(level0.Count <= compaction.LevelZeroCeiling, $"level 0 holds {level0.Count} objects past its ceiling of {compaction.LevelZeroCeiling}");
        long waiting = 0;
        foreach ((long min, long max) in level0.Where(held => held.Max <= greatest))
        {
            Assert.Equal(min / Value, max / Value);
            waiting++;
        }

        return new Stream(ids.Count, rewritten, met, waiting);
    }

    private readonly record struct Stream(long Appended, long Rewritten, long Met, long Waiting);

    [VortexRecord]
    public partial record struct Numbered(long Id, double Amount);
}
