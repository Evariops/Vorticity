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
        foreach ((SynopsisTableInfo table, int node) in pathTables.Tables)
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
}
