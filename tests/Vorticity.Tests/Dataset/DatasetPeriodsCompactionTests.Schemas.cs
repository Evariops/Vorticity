// Periods across a change of the schema: a job of periods counts its inputs in a first pass that reads the columns it
// counts alone, an object of an earlier schema reshaped into the current one, and plans each object it writes as it
// would plan the same rows written in the current schema from the start.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsCompactionTests
{
    /// <summary>
    /// An hour whose first quarters were appended before a text column and a variant column were added is cut and
    /// sealed as the same rows appended after them, the first quarters' new columns null: after each job, every object
    /// holds the same rows in the same slices, with the same shredding, and the rows read back the same.
    /// </summary>
    [Fact]
    public async Task AnHourWrittenUnderTwoSchemasIsPlannedAsOneWrittenUnderTheLast()
    {
        await using MemoryObjectStore evolvedStore = new MemoryObjectStore();
        await using MemoryObjectStore currentStore = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour, TimeSpan.FromMinutes(10));
        await using VortexDataset evolved = await VortexDataset.CreateAsync(evolvedStore, Request.Schema, options, Ct);
        await using VortexDataset current = await VortexDataset.CreateAsync(currentStore, Tagged.Schema, options, Ct);
        for (int quarter = 0; quarter < 3; quarter++)
        {
            Request[] requests = Quarter(quarter);
            await AppendAsync(evolved, requests);
            await AppendAsync(current, [.. requests.Select(r => new Tagged(r.At, r.RequestId, r.Status, r.Bytes, null, null))]);
        }

        await evolved.EvolveSchemaAsync(Tagged.Schema, cancellationToken: Ct);
        for (int quarter = 3; quarter < 6; quarter++)
        {
            Tagged[] tagged = [.. Quarter(quarter).Select((r, i) => new Tagged(
                r.At, r.RequestId, r.Status, r.Bytes, $"region-{i % 3}", Variant.Object(("route", Variant.From($"/r/{i % 4}")))))];
            await AppendAsync(evolved, tagged);
            await AppendAsync(current, tagged);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, Fanout = 100 };
        List<CompactionTrigger> triggers = [];
        while (true)
        {
            Assert.Equal(await PlannedAsync(current), await PlannedAsync(evolved));
            CompactionResult? done = await current.CompactAsync(compaction, Ct);
            Assert.Equal(done?.Trigger, (await evolved.CompactAsync(compaction, Ct))?.Trigger);
            if (done is null)
            {
                break;
            }

            triggers.Add(done.Trigger);
            Assert.True(triggers.Count < 50, $"compaction does not settle, {done.Trigger} again");
        }

        // The hour is sealed, its rows in the order they came, its slice table naming no order.
        Assert.Contains(CompactionTrigger.Seal, triggers);
        TreeEntry period = await FirstAsync(evolved, PeriodLevels.Periods);
        ObjectEntry sealedEntry = ObjectEntry.FromBytes(period.Value.Span);
        await using (ObjectLease lease = await evolved.RentAsync(sealedEntry, Ct))
        {
            SliceTable slices = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Empty(slices.Order);
            Assert.Equal(60, slices.Rows);
        }

        List<Tagged> rows = await evolved.Scan<Tagged>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(await current.Scan<Tagged>().ToRecordsAsync(Ct).ToListAsync(Ct), rows);
        Assert.Equal(45, rows.Count(r => r.Region is null && r.Attributes is null));
        Assert.True((await evolved.VerifyAsync(cancellationToken: Ct)).Holds);
    }

    /// <summary>Fifteen requests a minute apart from quarter <paramref name="quarter"/> of the hour past noon.</summary>
    private static Request[] Quarter(int quarter) =>
        [.. Enumerable.Range(0, 15).Select(i => Noon.AddMinutes(quarter * 15 + i)).Select((at, i) =>
            new Request(at, $"r-{quarter * 15 + i}", 200 + i, at.ToUnixTimeSeconds() * 7_919L % 1_000_003))];

    /// <summary>Appends <paramref name="records"/> as one object.</summary>
    private static async Task AppendAsync<TRecord>(VortexDataset dataset, TRecord[] records)
        where TRecord : IVortexRecord<TRecord>
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<TRecord>(records, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    /// <summary>
    /// Each object's level and rows, and for one a job wrote, its plan: its slices, the order inside them, its synopsis
    /// paths and its schema, which names what its variant column shreds; level 0 first.
    /// </summary>
    private static async Task<List<string>> PlannedAsync(VortexDataset dataset)
    {
        List<string> planned = [];
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry held in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(held.Value.Span);
                await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
                planned.Add(await SliceTable.ReadAsync(lease.File, Ct) is { } slices
                    ? $"{level}: {entry.Rows} rows in {string.Join(", ", slices.Slices)}, order [{string.Join(", ", slices.Order)}], "
                        + $"paths [{string.Join(", ", slices.Paths)}], {lease.File.DType}"
                    : $"{level}: {entry.Rows} rows");
            }
        }

        return planned;
    }

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, string RequestId, long Status, long Bytes, string? Region, Variant? Attributes);
}
