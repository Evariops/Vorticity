// The order a first slice large enough to judge settles: a column the object repeats but such a slice does not leaves it
// and is a measure; a slice too small to judge leaves the object's count to decide.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisTableTests
{
    /// <summary>
    /// Two slices of 70 000 rows, sorted by a region of four values, a status of five and a request drawn from 100 000
    /// values, which the object's 140 000 rows repeat but a slice does not: the first slice settles the order without the
    /// request, which every table measures. Over slices of 7 000 rows, fewer than a slice judges a column from, the request
    /// stays in the order the object's count gave it, and is a measure too of the tables it is no key of.
    /// </summary>
    [Fact]
    public async Task AColumnALargeSliceDoesNotRepeatLeavesTheOrderAndIsMeasured()
    {
        Assert.True(70_000 >= SortedSlices.JudgedRows && 7_000 < SortedSlices.JudgedRows);
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-judged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            (IReadOnlyList<int> large, List<SynopsisTableInfo> largeTables, DType schema) = await JudgedAsync(scratch, sliceRows: 70_000);
            int region = schema.IndexOfField(nameof(Spread.Region));
            int status = schema.IndexOfField(nameof(Spread.Status));
            int request = schema.IndexOfField(nameof(Spread.Request));
            Assert.Equal([region, status], large.Order());
            Assert.NotEmpty(largeTables);
            Assert.All(largeTables, table => Assert.Contains(request, table.Measures));
            Assert.All(largeTables, table => Assert.DoesNotContain(request, table.Keys));

            (IReadOnlyList<int> small, List<SynopsisTableInfo> smallTables, _) = await JudgedAsync(scratch, sliceRows: 7_000);
            Assert.Equal([region, status, request], small.Order());
            Assert.Contains(smallTables, table => table.Keys.Contains(request));
            Assert.Contains(smallTables, table => !table.Keys.Contains(request) && table.Measures.Contains(request));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// 140 000 rows over two hours in slices of <paramref name="sliceRows"/>, sorted by region, status and request as the
    /// first slice settles it, with their synopsis: the order settled, and every table the synopsis kept.
    /// </summary>
    private static async Task<(IReadOnlyList<int> Order, List<SynopsisTableInfo> Tables, DType Schema)> JudgedAsync(string scratch, int sliceRows)
    {
        const int Rows = 140_000;
        Random random = new Random(19);
        Spread[] rows = new Spread[Rows];
        long step = TimeSpan.FromHours(2).Ticks / Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Spread(Noon.AddTicks(i * step), $"region-{random.Next(4)}", 200 + (random.Next(5) * 100), random.Next(100_000), random.NextInt64(1_000_000));
        }

        string plain = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Spread>(plain))
        {
            await writer.WriteAsync<Spread>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using VortexFile file = await VortexFile.OpenAsync(plain, cancellationToken: Ct);
        DType schema = file.DType;
        int instant = schema.IndexOfField(nameof(Spread.At));
        long micros = Noon.ToUnixTimeMilliseconds() * 1000;
        int sliceCount = Rows / sliceRows;
        long width = TimeSpan.FromHours(2).Ticks / 10 / sliceCount;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, sliceCount).Select(s => new TimeSlice(micros + (s * width), micros + ((s + 1) * width), (long)s * sliceRows, sliceRows))],
            [schema.IndexOfField(nameof(Spread.Region)), schema.IndexOfField(nameof(Spread.Status)), schema.IndexOfField(nameof(Spread.Request))]);

        string path = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.WithBudgetPerMille(10_000),
            SynopsisColumns = [nameof(Spread.Region), nameof(Spread.Status), nameof(Spread.Request)],
            ScratchDirectory = scratch,
        };

        await using VortexFileWriter sorted = VortexSession.Default.CreateWriter<Spread>(path, options);
        using SortedSlices slices = new SortedSlices(table, instant, schema);
        using SynopsisTables tables = new SynopsisTables(schema, table.Unit, SynopsisTables.Measures(schema, instant), sorted, slices: table.Slices);
        int next = 0;
        await foreach (RecordBatch batch in file.Scan<Spread>().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                await slices.AppendAsync(batch, async (slice, token) =>
                {
                    await tables.AddSliceAsync(slice.Batch, table.Slices[next++].Start, slices.Order, slice.Codes, cancellationToken: token);
                    await sorted.WriteBatchAsync(slice.Batch, token);
                }, Ct);
            }
        }

        await slices.DrainAsync();
        List<SynopsisTableInfo> kept = [.. (await tables.FinishAsync(Ct)).Select(t => t.Table)];
        sorted.DeclareSlices(nameof(Spread.At), slices.Table);
        await sorted.CompleteAsync(Ct);
        return (slices.Order, kept, schema);
    }
}
