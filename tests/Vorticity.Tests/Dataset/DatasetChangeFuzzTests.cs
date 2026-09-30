using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// Seeded schedules of appends, deletes, updates, compactions and changes of schema, from two
/// handles of which one often acts on the version it last saw: an append from a handle that has not
/// seen a change lands an object of the earlier schema after it. After every step the rows read as
/// the current schema, a count under a filter and the key cursor's walk both ways equal a model of
/// the rows kept in plain C#, which applies each change as its commit lands.
/// </summary>
public sealed class DatasetChangeFuzzTests
{
    private static readonly string[] Sites = ["Paris", "Lyon", "Nice", "Lille"];

    [Theory]
    [InlineData(7)]
    [InlineData(19)]
    [InlineData(31)]
    [InlineData(43)]
    public async Task AScheduleOfChangesAnswersAsTheModel(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xF022_C4A6,
            ClusteringKey = ["Key"],
            Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
        };
        await using VortexDataset reader = await VortexDataset.CreateAsync(store, MeterV1.Schema, options, ct);
        await using VortexDataset first = await VortexDataset.OpenAsync(store, options, ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, ct);
        VortexDataset[] handles = [first, second];
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 4 << 10 };

        Random random = new Random(seed);
        List<Row> model = [];
        int stage = 1;
        long nextKey = 0;
        int deletes = 0;
        int updates = 0;
        int compactions = 0;
        for (int step = 0; step < 40; step++)
        {
            VortexDataset handle = handles[random.Next(2)];
            if (random.Next(2) == 0)
            {
                await handle.RefreshAsync(ct);
            }

            int roll = model.Count == 0 ? 0 : random.Next(100);
            if (roll < 40)
            {
                // Written in whatever schema the handle holds, which may be one the dataset has left.
                int count = 50 + random.Next(150);
                List<Row> rows = [];
                for (int i = 0; i < count; i++)
                {
                    long key = random.Next(3) == 0 && model.Count > 0 ? model[random.Next(model.Count)].Key : nextKey++;
                    rows.Add(new Row(key, key * 7 % 1_000, Sites[(int)(key % Sites.Length)], null));
                }

                await AppendAsync(handle, rows, ct);
                model.AddRange(rows);
            }
            else if (roll < 55)
            {
                await handle.RefreshAsync(ct);
                (long low, long high) = Range(model, random);
                RowChangeResult deleted = await DeleteAsync(handle, low, high, ct);
                Assert.Equal(model.Count(row => row.Key >= low && row.Key < high), deleted.Rows);
                model.RemoveAll(row => row.Key >= low && row.Key < high);
                deletes++;
            }
            else if (roll < 70)
            {
                await handle.RefreshAsync(ct);
                (long low, long high) = Range(model, random);
                RowChangeResult updated = await UpdateAsync(handle, low, high, stage, ct);
                Assert.Equal(model.Count(row => row.Key >= low && row.Key < high), updated.Rows);
                model = model.ConvertAll(row => row.Key >= low && row.Key < high ? Updated(row, stage) : row);
                updates++;
            }
            else if (roll < 88)
            {
                if (await handle.CompactAsync(compaction, ct) is { Outcome: OperationOutcome.Applied })
                {
                    compactions++;
                }
            }
            else if (stage < 3)
            {
                await handle.RefreshAsync(ct);
                if (stage == 1)
                {
                    await handle.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);
                }
                else
                {
                    await handle.EvolveSchemaAsync(MeterV3.Schema, cancellationToken: ct);
                    model = model.ConvertAll(row => row with { Place = null });
                }

                stage++;
            }

            await reader.RefreshAsync(ct);
            List<Row> expected = Sorted(model, stage);
            Assert.Equal(expected, Sorted(await RowsAsync(reader, stage, ct), stage));
            Assert.Equal(expected.Count, reader.RowCount);
            long pivot = expected.Count == 0 ? 0 : expected[expected.Count / 2].Reading;
            Assert.Equal(expected.Count(row => row.Reading >= pivot), await CountAsync(reader, stage, pivot, ct));

            List<long> keys = [.. expected.Select(row => row.Key).Order()];
            await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(reader, ct);
            List<long> up = [];
            for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
            {
                up.Add(cursor.Key.SignedValue);
            }

            List<long> down = [];
            for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PrevAsync(ct))
            {
                down.Add(cursor.Key.SignedValue);
            }

            down.Reverse();
            Assert.Equal(keys, up);
            Assert.Equal(keys, down);
        }

        Assert.True((await reader.VerifyAsync(cancellationToken: ct)).Holds);
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET CHANGE FUZZ seed {seed}: version {reader.Version}, schema {stage}, {reader.RowCount} rows in {reader.ObjectCount} objects, {deletes} delete(s), {updates} update(s), {compactions} compaction(s).\n"));
    }

    /// <summary>A row as the model holds it: the widest form of each column, a place of null once the column is dropped.</summary>
    private readonly record struct Row(long Key, long Reading, string? Place, double? Temperature);

    /// <summary>The schema a handle holds: 1, 2 or 3.</summary>
    private static int StageOf(VortexDataset handle) =>
        handle.Schema.Count == 3 && handle.Schema[2].Name == "Site" ? 1 : handle.Schema.Count == 4 ? 2 : 3;

    private static Row Updated(Row row, int stage) =>
        stage == 1 ? row with { Reading = row.Reading + 1 } : row with { Reading = row.Reading + 1_000_000, Temperature = row.Key % 3 - 1.5 };

    private static (long Low, long High) Range(List<Row> model, Random random)
    {
        long low = model[random.Next(model.Count)].Key;
        return (low, low + 1 + random.Next(60));
    }

    private static async Task AppendAsync(VortexDataset handle, List<Row> rows, CancellationToken ct)
    {
        await using ObjectDraft draft = handle.StartObject();
        switch (StageOf(handle))
        {
            case 1:
                await draft.Writer.WriteAsync<MeterV1>([.. rows.Select(row => new MeterV1(row.Key, (int)row.Reading, row.Place!))], ct);
                break;
            case 2:
                await draft.Writer.WriteAsync<MeterV2>([.. rows.Select(row => new MeterV2(row.Key, row.Reading, row.Place, row.Temperature))], ct);
                break;
            default:
                await draft.Writer.WriteAsync<MeterV3>([.. rows.Select(row => new MeterV3(row.Key, row.Reading, row.Temperature))], ct);
                break;
        }

        await handle.AppendAsync(draft, ct);
    }

    private static ValueTask<RowChangeResult> DeleteAsync(VortexDataset handle, long low, long high, CancellationToken ct) =>
        StageOf(handle) switch
        {
            1 => handle.DeleteAsync<MeterV1>(r => r.Key >= low & r.Key < high, ct),
            2 => handle.DeleteAsync<MeterV2>(r => r.Key >= low & r.Key < high, ct),
            _ => handle.DeleteAsync<MeterV3>(r => r.Key >= low & r.Key < high, ct),
        };

    private static ValueTask<RowChangeResult> UpdateAsync(VortexDataset handle, long low, long high, int stage, CancellationToken ct) =>
        stage switch
        {
            1 => handle.UpdateAsync<MeterV1>(r => r.Key >= low & r.Key < high, row => row with { Reading = row.Reading + 1 }, ct),
            2 => handle.UpdateAsync<MeterV2>(
                r => r.Key >= low & r.Key < high, row => row with { Reading = row.Reading + 1_000_000, Temperature = row.Key % 3 - 1.5 }, ct),
            _ => handle.UpdateAsync<MeterV3>(
                r => r.Key >= low & r.Key < high, row => row with { Reading = row.Reading + 1_000_000, Temperature = row.Key % 3 - 1.5 }, ct),
        };

    private static async Task<List<Row>> RowsAsync(VortexDataset reader, int stage, CancellationToken ct) => stage switch
    {
        1 => (await reader.Scan<MeterV1>().ToRecordsAsync(ct).ToListAsync(ct)).ConvertAll(row => new Row(row.Key, row.Reading, row.Site, null)),
        2 => (await reader.Scan<MeterV2>().ToRecordsAsync(ct).ToListAsync(ct)).ConvertAll(row => new Row(row.Key, row.Reading, row.Place, row.Temperature)),
        _ => (await reader.Scan<MeterV3>().ToRecordsAsync(ct).ToListAsync(ct)).ConvertAll(row => new Row(row.Key, row.Reading, null, row.Temperature)),
    };

    private static async Task<long> CountAsync(VortexDataset reader, int stage, long pivot, CancellationToken ct) => stage switch
    {
        1 => await reader.Scan<MeterV1>().Where(r => r.Reading >= (int)pivot).CountAsync(ct),
        2 => await reader.Scan<MeterV2>().Where(r => r.Reading >= pivot).CountAsync(ct),
        _ => await reader.Scan<MeterV3>().Where(r => r.Reading >= pivot).CountAsync(ct),
    };

    private static List<Row> Sorted(IEnumerable<Row> rows, int stage) =>
        [.. rows
            .Select(row => stage == 3 ? row with { Place = null } : row)
            .OrderBy(row => row.Key).ThenBy(row => row.Reading).ThenBy(row => row.Place, StringComparer.Ordinal).ThenBy(row => row.Temperature)];
}
