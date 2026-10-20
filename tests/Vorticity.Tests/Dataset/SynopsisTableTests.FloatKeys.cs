// Synopsis tables keyed by a float: a slice codes a float's zeros of either sign as one value and its NaNs as
// another, so the merges of the hours and of the object hold one row for each, under the first occurrence's bits.
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
    private static readonly long NegativeZero = BitConverter.DoubleToInt64Bits(-0.0);

    /// <summary>
    /// Three slices over two hours of a level that is -0, +0, 1.5 or NaN, the first slice's first zero negative and
    /// the others' positive: the coarse tables of each hour and of the object hold one row for the zeros and one for
    /// the NaNs, counting every row of them, the zero's key the bits of its first occurrence.
    /// </summary>
    [Fact]
    public async Task AFloatKeysItsZerosAndItsNaNsAsOneValueEach()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-floatkeys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            const int sliceRows = 2_000;
            double[][] cycles = [[-0.0, 1.5, double.NaN, 0.0], [0.0, 1.5, double.NaN, -0.0], [0.0, double.NaN]];
            long[] starts = [0, TimeSpan.FromMinutes(30).Ticks / 10, TimeSpan.FromMinutes(70).Ticks / 10];
            List<Leveled> rows = [];
            for (int s = 0; s < cycles.Length; s++)
            {
                for (int i = 0; i < sliceRows; i++)
                {
                    rows.Add(new Leveled(Noon.AddTicks((starts[s] + i) * 10), cycles[s][i % cycles[s].Length], i));
                }
            }

            string plain = Path.Combine(scratch, "plain.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Leveled>(plain))
            {
                await writer.WriteAsync<Leveled>(rows.ToArray(), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(plain, cancellationToken: Ct);
            DType schema = file.DType;
            int instant = schema.IndexOfField(nameof(Leveled.At));
            long micros = Noon.ToUnixTimeMilliseconds() * 1000;
            SliceTable table = new SliceTable(
                TimeUnit.Microseconds,
                [.. Enumerable.Range(0, cycles.Length).Select(s => new TimeSlice(micros + starts[s], micros + starts[s] + 60_000_000, (long)s * sliceRows, sliceRows))],
                [schema.IndexOfField(nameof(Leveled.Level))]);
            string path = Path.Combine(scratch, "sliced.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Leveled>(path, new VortexWriteOptions
            {
                Indexes = IndexPolicy.None.WithBudgetPerMille(100_000),
                SynopsisColumns = [nameof(Leveled.Level)],
                ScratchMemoryBytes = 0,
                ScratchDirectory = scratch,
            }))
            {
                using SortedSlices slices = new SortedSlices(table, instant, schema);
                using SynopsisTables tables = new SynopsisTables(
                    schema, table.Unit, SynopsisTables.Measures(schema, instant, table.Order), writer, slices: table.Slices);
                int next = 0;
                await foreach (RecordBatch batch in file.Scan<Leveled>().ToBatchesAsync(Ct))
                {
                    using (batch)
                    {
                        await slices.AppendAsync(batch, async (sorted, token) =>
                        {
                            await tables.AddSliceAsync(sorted, table.Slices[next++].Start, slices.Order, slices.SortedCodes, cancellationToken: token);
                            await writer.WriteBatchAsync(sorted, token);
                        }, Ct);
                    }
                }

                writer.DeclareSlices(nameof(Leveled.At), slices.Table);
                writer.DeclareSynopsisTables(await tables.FinishAsync(Ct));
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile sliced = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            List<(SynopsisTableInfo Info, List<TableRow> Rows)> read = await TablesAsync(sliced);
            List<TableRow> hourly = Assert.Single(read, t => t.Info is { Kind: SynopsisTableKind.Coarse, Bucket: SynopsisBucket.Hour, Keys.Length: 1 }).Rows;
            List<TableRow> whole = Assert.Single(read, t => t.Info is { Kind: SynopsisTableKind.Coarse, Bucket: SynopsisBucket.Object, Keys.Length: 1 }).Rows;

            // The first hour: two slices, a quarter of each's rows of each value; the second: half its rows zeros.
            long firstHour = hourly[0].Bucket;
            Assert.Equal(
                [(firstHour, "0", 2_000L), (firstHour, "1.5", 1_000L), (firstHour, "NaN", 1_000L), (firstHour + 3_600_000_000, "0", 1_000L), (firstHour + 3_600_000_000, "NaN", 1_000L)],
                hourly.Select(r => (r.Bucket, Level(r), r.Count)).OrderBy(r => r.Bucket).ThenBy(r => r.Item2, StringComparer.Ordinal));
            Assert.Equal([("0", 3_000L), ("1.5", 1_000L), ("NaN", 2_000L)], whole.Select(r => (Level(r), r.Count)).OrderBy(r => r.Item1, StringComparer.Ordinal));

            // A zero's key is its first occurrence's: negative in the first hour and the object, positive in the second.
            Assert.Equal(NegativeZero, Bits(hourly.Single(r => r.Bucket == firstHour && Level(r) == "0")));
            Assert.Equal(0L, Bits(hourly.Single(r => r.Bucket != firstHour && Level(r) == "0")));
            Assert.Equal(NegativeZero, Bits(whole.Single(r => Level(r) == "0")));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        static string Level(TableRow row) => row.Keys[0] is double level
            ? double.IsNaN(level) ? "NaN" : level == 0 ? "0" : level.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";

        static long Bits(TableRow row) => BitConverter.DoubleToInt64Bits((double)row.Keys[0]!);
    }

    [VortexRecord]
    public partial record struct Leveled(DateTimeOffset At, double Level, long Count);
}
