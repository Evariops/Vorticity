// The synopsis on paths: a key a variant column shreds whose values repeat is a synopsis column, sorted
// and counted as a declared column is, and a numeric key whose values rarely repeat is summed as a measure.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisPathTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The same requests twice: once with the route and the latency as declared columns, once as keys of
    /// an attributes variant the objects shred. The sealed hour of the second orders its slices by the
    /// route's path, sums the latency's path, and holds, table for table, the rows the first holds: the
    /// same routes, counts, values, sums, least and greatest.
    /// </summary>
    [Fact]
    public async Task APathCountsAsADeclaredColumnOfTheSameValues()
    {
        Random random = new Random(7);
        (DateTimeOffset At, string Route, long Latency)[] rows = [.. Enumerable.Range(0, 6_400).Select(i =>
            (Noon.AddSeconds(i * 1.25), $"/r/{random.Next(9)}", random.NextInt64(1_000_000)))];

        await using MemoryObjectStore declaredStore = new MemoryObjectStore();
        await using MemoryObjectStore pathStore = new MemoryObjectStore();
        await using VortexDataset declared = await SealedAsync(declaredStore, Declared.Schema, async draft =>
        {
            foreach ((DateTimeOffset at, string route, long latency)[] part in rows.Chunk(400))
            {
                await draft(writer => writer.WriteAsync<Declared>([.. part.Select(r => new Declared(r.at, r.route, r.latency))], Ct));
            }
        });
        await using VortexDataset pathed = await SealedAsync(pathStore, Pathed.Schema, async draft =>
        {
            foreach ((DateTimeOffset at, string route, long latency)[] part in rows.Chunk(400))
            {
                await draft(writer => writer.WriteAsync<Pathed>(
                    [.. part.Select(r => new Pathed(r.at, Variant.Object(("route", Variant.From(r.route)), ("latency", Variant.From(r.latency)))))], Ct));
            }
        });

        ObjectEntry declaredHour = await FirstPeriodAsync(declared);
        ObjectEntry pathedHour = await FirstPeriodAsync(pathed);
        await using ObjectLease left = await declared.RentAsync(declaredHour, Ct);
        await using ObjectLease right = await pathed.RentAsync(pathedHour, Ct);
        SliceTable declaredSlices = (await SliceTable.ReadAsync(left.File, Ct))!;
        SliceTable pathSlices = (await SliceTable.ReadAsync(right.File, Ct))!;
        int attributes = right.File.DType.IndexOfField(nameof(Pathed.Attributes));
        Assert.Equal([left.File.DType.IndexOfField(nameof(Declared.Route))], declaredSlices.Order);
        int route = pathSlices.Paths.ToList().FindIndex(p => p.Key == "route");
        int latency = pathSlices.Paths.ToList().FindIndex(p => p.Key == "latency");
        Assert.Equal(new SynopsisPath(attributes, "route", VariantClass.String), pathSlices.Paths[route]);
        Assert.Equal(new SynopsisPath(attributes, "latency", VariantClass.Integer), pathSlices.Paths[latency]);
        Assert.Contains(SynopsisPath.Entry(route), pathSlices.Order);
        Assert.DoesNotContain(SynopsisPath.Entry(latency), pathSlices.Order);

        using SynopsisTableSet declaredTables = await SynopsisTableInfo.ReadAsync(left.File, Ct);
        using SynopsisTableSet pathTables = await SynopsisTableInfo.ReadAsync(right.File, Ct);
        int compared = 0;
        foreach ((SynopsisTableInfo table, TableChunk _, int node) in pathTables.Tables)
        {
            Assert.Equal([SynopsisPath.Entry(latency)], table.Measures);
            Assert.All(table.Keys, key => Assert.Equal(SynopsisPath.Entry(route), key));
            if (declaredTables.Tables.FirstOrDefault(t => t.Table.Kind == table.Kind && t.Table.Bucket == table.Bucket && t.Table.Keys.Length == table.Keys.Length) is
                { Table: { } twin, Node: int twinNode })
            {
                Assert.Equal(twin.Rows, table.Rows);
                Assert.Equal(Rows(declaredTables.Arena, twinNode), Rows(pathTables.Arena, node));
                compared++;
            }
        }

        Assert.True(compared >= 2, $"only {compared} tables to compare");
    }

    /// <summary>
    /// Calls whose attributes hold a route on every row, a flag on one row in three and a count of retries on
    /// one in four: the objects shred the route and leave the flag and the retries in the residual. The sealed
    /// hour counts the flag's values per hour and per slice, as many as the parsed rows that hold each, and sums
    /// the retries over the rows that hold them, in its value counts as in its other tables.
    /// </summary>
    [Fact]
    public async Task APathLeftInTheResidualIsCountedFromTheParsedRows()
    {
        Random random = new Random(11);
        Pathed[] rows = [.. Enumerable.Range(0, 6_400).Select(i =>
        {
            List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(9)}"))];
            if (i % 3 == 0)
            {
                fields.Add(("flag", Variant.From(random.Next(2) == 0 ? "on" : "off")));
            }

            if (i % 4 == 0)
            {
                fields.Add(("retries", Variant.From(random.NextInt64(1_000_000))));
            }

            return new Pathed(Noon.AddSeconds(i * 1.25), Variant.Object([.. fields]));
        })];

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await SealedAsync(store, Pathed.Schema, async draft =>
        {
            foreach (Pathed[] part in rows.Chunk(400))
            {
                await draft(writer => writer.WriteAsync<Pathed>(part, Ct));
            }
        });

        await using ObjectLease lease = await dataset.RentAsync(await FirstPeriodAsync(dataset), Ct);
        SliceTable slices = (await SliceTable.ReadAsync(lease.File, Ct))!;
        int attributes = lease.File.DType.IndexOfField(nameof(Pathed.Attributes));
        List<SynopsisPath> paths = [.. slices.Paths];
        int flag = paths.IndexOf(new SynopsisPath(attributes, "flag", VariantClass.String, Residual: true));
        int retries = paths.IndexOf(new SynopsisPath(attributes, "retries", VariantClass.Integer, Residual: true));
        Assert.True(flag >= 0 && retries >= 0, string.Join(", ", paths));
        Assert.DoesNotContain(SynopsisPath.Entry(flag), slices.Order);

        // What the parsed rows hold: per flag, the rows, and the retries' count and sum.
        List<Pathed> stored = await lease.File.Scan<Pathed>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Dictionary<string, (long Rows, long Retries, long Sum)> expected = [];
        foreach (Pathed row in stored)
        {
            if (row.Attributes["flag"]?.AsString() is not { } value)
            {
                continue;
            }

            long? count = row.Attributes["retries"]?.AsInt64();
            (long seen, long held, long sum) = expected.GetValueOrDefault(value);
            expected[value] = (seen + 1, held + (count is null ? 0 : 1), sum + (count ?? 0));
        }

        // The value counts of the hour, a row a value, and those of its slices, a row a value of each slice, summed.
        using SynopsisTableSet tables = await SynopsisTableInfo.ReadAsync(lease.File, Ct);
        foreach (SynopsisBucket bucket in (SynopsisBucket[])[SynopsisBucket.Hour, SynopsisBucket.Slice])
        {
            (SynopsisTableInfo info, TableChunk _, int node) = Assert.Single(
                tables.Tables, t => t.Table.Kind == SynopsisTableKind.Values && t.Table.Bucket == bucket && t.Table.Keys.SequenceEqual([SynopsisPath.Entry(flag)]));
            Assert.Contains(SynopsisPath.Entry(retries), info.Measures);
            int measure = Array.IndexOf(info.Measures, SynopsisPath.Entry(retries));
            CanonicalNode table = tables.Arena.GetNode(node);
            Dictionary<string, (long Rows, long Retries, long Sum)> counted = [];
            for (int row = 0; row < info.Rows; row++)
            {
                // The bucket, the flag, the count, then each measure's values, two words of its sum, least and greatest.
                int states = 3 + (5 * measure);
                string value = Encoding.UTF8.GetString(ColumnData.Bytes(tables.Arena, table.GetFieldIndex(1), row));
                (long seen, long held, long sum) = counted.GetValueOrDefault(value);
                counted[value] = (
                    seen + ColumnData.Values<long>(tables.Arena, table.GetFieldIndex(2))[row],
                    held + ColumnData.Values<long>(tables.Arena, table.GetFieldIndex(states))[row],
                    sum + ColumnData.Values<long>(tables.Arena, table.GetFieldIndex(states + 1))[row]);
            }

            Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), counted.OrderBy(e => e.Key, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// A schedule of appends in time order, deletes of a status and compactions over two hours and a half:
    /// at every version, each object that counts the flag left in the residual counts, per hour, the rows of
    /// its own that hold each value, those a delete only marked included, and sums their retries.
    /// </summary>
    [Fact]
    public async Task AScheduleCountsTheResidualPathsAtEveryVersion()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Tagged.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Tagged.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        Random random = new Random(13);
        DateTimeOffset at = Noon;
        HashSet<UInt128> seen = [];
        int checkedObjects = 0;
        for (int round = 0; round < 60; round++)
        {
            Tagged[] rows = [.. Enumerable.Range(0, 100 + random.Next(150)).Select(i =>
            {
                at = at.AddSeconds(1 + random.Next(2));
                List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(5)}"))];
                if (random.Next(3) == 0)
                {
                    fields.Add(("flag", Variant.From(random.Next(2) == 0 ? "on" : "off")));
                }

                if (random.Next(4) == 0)
                {
                    fields.Add(("retries", Variant.From(random.NextInt64(1_000))));
                }

                return new Tagged(at, 200 + random.Next(20), Variant.Object([.. fields]));
            })];
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Tagged>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            if (random.Next(10) == 0)
            {
                await dataset.DeleteAsync(VortexExpr.Parse($"Status = {200 + random.Next(20)}"), Ct);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, Fanout = 3 }, Ct) is not null)
            {
            }

            checkedObjects += await ResidualCountsAsync(dataset, seen);
        }

        Assert.True(checkedObjects > 2, $"only {checkedObjects} objects counted the flag");
    }

    /// <summary>
    /// Every object not seen before that counts the flag in its residual: its value counts per hour equal what
    /// its own rows hold. Returns how many it checked.
    /// </summary>
    private static async Task<int> ResidualCountsAsync(VortexDataset dataset, HashSet<UInt128> seen)
    {
        int checkedObjects = 0;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry stored = ObjectEntry.FromBytes(entry.Value.Span);
                if (!seen.Add(stored.Uid))
                {
                    continue;
                }

                await using ObjectLease lease = await dataset.RentAsync(stored, Ct);
                if (await SliceTable.ReadAsync(lease.File, Ct) is not { } slices
                    || slices.Paths.ToList().FindIndex(p => p.Key == "flag" && p.Residual) is not (>= 0 and var flag))
                {
                    continue;
                }

                using SynopsisTableSet tables = await SynopsisTableInfo.ReadAsync(lease.File, Ct);
                if (tables.Tables.FirstOrDefault(t => t.Table.Kind == SynopsisTableKind.Values && t.Table.Keys.SequenceEqual([SynopsisPath.Entry(flag)])) is not
                    { Table: { } info, Node: int node })
                {
                    continue;
                }

                // The rows of each slice, and the hour of its start.
                List<Tagged> rows = await lease.File.Scan<Tagged>().ToRecordsAsync(Ct).ToListAsync(Ct);
                Dictionary<(long, string), long> expected = [];
                foreach (TimeSlice slice in slices.Slices)
                {
                    long hour = TimeSlicer.Floor(slice.Start, slices.Unit, SliceWidth.Hour);
                    foreach (Tagged row in rows.GetRange((int)slice.FirstRow, (int)slice.Rows))
                    {
                        if (row.Attributes["flag"]?.AsString() is { } value)
                        {
                            expected[(hour, value)] = expected.GetValueOrDefault((hour, value)) + 1;
                        }
                    }
                }

                CanonicalNode table = tables.Arena.GetNode(node);
                Dictionary<(long, string), long> counted = [];
                for (int row = 0; row < info.Rows; row++)
                {
                    long hour = ColumnData.Values<long>(tables.Arena, table.GetFieldIndex(0))[row];
                    string value = Encoding.UTF8.GetString(ColumnData.Bytes(tables.Arena, table.GetFieldIndex(1), row));
                    counted[(hour, value)] = ColumnData.Values<long>(tables.Arena, table.GetFieldIndex(2))[row];
                }

                Assert.Equal(expected.OrderBy(e => e.Key), counted.OrderBy(e => e.Key));
                checkedObjects++;
            }
        }

        return checkedObjects;
    }

    /// <summary>A dataset of periods of an hour with a budget as large as the data, written by <paramref name="write"/>, then compacted until its first hour is sealed.</summary>
    private static async Task<VortexDataset> SealedAsync(
        MemoryObjectStore store, VortexSchema schema, Func<Func<Func<VortexFileWriter, ValueTask>, Task>, Task> write)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, new DatasetOptions
        {
            ClusteringKey = ["At"],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        await write(async append =>
        {
            await using ObjectDraft draft = dataset.StartObject();
            await append(draft.Writer);
            await dataset.AppendAsync(draft, Ct);
        });
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    private static async Task<ObjectEntry> FirstPeriodAsync(VortexDataset dataset)
    {
        await foreach (TreeEntry entry in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            return ObjectEntry.FromBytes(entry.Value.Span);
        }

        throw new InvalidOperationException("No period was sealed.");
    }

    /// <summary>A table's rows as text: each column's value, row by row, a key's text or a state's number.</summary>
    private static List<string> Rows(CanonicalArena arena, int node)
    {
        CanonicalNode table = arena.GetNode(node);
        int count = table.Length;
        List<string> rows = [.. Enumerable.Repeat(string.Empty, count)];
        for (int field = 0; field < table.DType.FieldCount; field++)
        {
            int column = arena.GetNode(node).GetFieldIndex(field);
            DType dtype = table.DType.GetField(field);
            for (int row = 0; row < count; row++)
            {
                string value = !ArenaWords.IsValid(arena, EncodedForms.Canonical(arena, column), row)
                    ? "null"
                    : dtype.Kind == DTypeKind.Utf8
                        ? Encoding.UTF8.GetString(ColumnData.Bytes(arena, column, row))
                        : ColumnData.Values<long>(arena, column)[row].ToString(System.Globalization.CultureInfo.InvariantCulture);
                rows[row] += value + " ";
            }
        }

        return rows;
    }

    [VortexRecord]
    public partial record struct Declared(DateTimeOffset At, string Route, long Latency);

    [VortexRecord]
    public partial record struct Pathed(DateTimeOffset At, Variant Attributes);

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, long Status, Variant Attributes);
}
