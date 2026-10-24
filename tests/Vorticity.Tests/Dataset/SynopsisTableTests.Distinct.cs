// Distinct sketches past the depth sketched: a key the first slice's groups are too many to sketch by, but whose
// values do not make each row a group, comes to the tables of sketches above it from the hour's merge.
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
    private const int WideRows = 120_000;

    private const int WideSlices = 6;

    /// <summary>
    /// Two hours of 120 000 rows in six slices of 20 000, sorted by a region (4 values) then a host (2 000): the first
    /// slice holds some 7 800 groups of both, past the 4 096 a table of sketches is kept for, and fewer than half its
    /// rows, so that the tables of sketches are of the region alone, and the host's distinct sketches come from the
    /// hours' merges; each row of each table holds what the rows it covers give.
    /// </summary>
    [Fact]
    public async Task AKeyPastTheDepthSketchedComesToItsTablesFromTheHoursMerge()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-distinct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            string path = await WideAsync(scratch);
            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            SliceTable slices = (await SliceTable.ReadAsync(file, Ct))!;
            DType dtype = file.DType;
            int host = dtype.IndexOfField(nameof(Wide.Host));
            List<(SynopsisTableInfo Info, List<TableRow> Rows)> tables = await TablesAsync(file);
            Assert.DoesNotContain(tables, t => t.Info.Kind == SynopsisTableKind.Sketches && t.Info.Keys.Length == 2);
            Assert.Contains(tables, t => t.Info is { Kind: SynopsisTableKind.Sketches, Bucket: SynopsisBucket.Hour, Keys.Length: 1 }
                && t.Info.Distinct.Contains(host) && t.Info.Lost.Length == 0);

            List<object?[]> fields = [];
            await foreach (Wide row in file.Scan<Wide>().ToRecordsAsync(Ct))
            {
                object?[] values = new object?[dtype.FieldCount];
                values[dtype.IndexOfField(nameof(Wide.Region))] = row.Region;
                values[host] = row.Host;
                values[dtype.IndexOfField(nameof(Wide.Duration))] = row.Duration;
                fields.Add(values);
            }

            List<long> starts = [];
            foreach (TimeSlice slice in slices.Slices)
            {
                starts.AddRange(Enumerable.Repeat(slice.Start, (int)slice.Rows));
            }

            foreach ((SynopsisTableInfo info, List<TableRow> rows) in tables.Where(t => t.Info.Kind == SynopsisTableKind.Sketches))
            {
                Kind[] kinds = Kinds(dtype, info.Measures);
                List<TableRow> units = [];
                for (int row = 0; row < fields.Count; row++)
                {
                    long bucket = info.Bucket == SynopsisBucket.Hour ? TimeSlicer.Floor(starts[row], slices.Unit, SliceWidth.Hour) : 0;
                    units.Add(Unit(
                        bucket, [.. info.Keys.Select(k => fields[row][k])], [.. info.Measures.Select(m => fields[row][m])], kinds, sketched: true,
                        [.. Whole(info).Select(k => ValueHash(fields[row][k], dtype.GetField(k)))]));
                }

                List<TableRow> expected = Merge(units, static row => row.Bucket, [.. Enumerable.Range(0, info.Keys.Length)], kinds);
                Assert.Equal(Show(expected, kinds), Show(rows, kinds));
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The wide rows of 4 000 hosts: the hosts' values per slice, some 23 800 rows, lie in chunks of 16 384 rows at most,
    /// a quarter of another table's, so that a question of one host reads the rows of its hour in one of them.
    /// </summary>
    [Fact]
    public async Task AChunkOfATableOfValuesPerSliceHoldsAQuarterOfAnotherTablesRows()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-bounded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            string path = await WideAsync(scratch, hosts: 4_000);
            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            int host = file.DType.IndexOfField(nameof(Wide.Host));
            List<(SynopsisTableInfo Info, List<TableRow> Rows)> chunks = [.. (await TablesAsync(file))
                .Where(t => t.Info is { Kind: SynopsisTableKind.Values, Bucket: SynopsisBucket.Slice } && t.Info.Keys.SequenceEqual([host]))];
            Assert.True(chunks.Count >= 2, $"{chunks.Count} chunks of the hosts' values per slice");
            Assert.All(chunks, chunk => Assert.InRange(chunk.Rows.Count, 1, SynopsisTables.BoundedChunkRows));
            Assert.True(chunks.Sum(chunk => chunk.Rows.Count) > SynopsisTables.BoundedChunkRows, "the table fits one chunk");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The wide rows, of <paramref name="hosts"/> hosts, written with their synopsis, every table kept, into <paramref name="scratch"/>;
    /// the groups of sketches of an hour or of the object held to <paramref name="heldSketchBytes"/> when it is given.
    /// </summary>
    private static async Task<string> WideAsync(string scratch, int hosts = 2_000, long? heldSketchBytes = null, List<WriteReport>? reports = null)
    {
        Random random = new Random(29);
        Wide[] rows = new Wide[WideRows];
        long step = TimeSpan.FromHours(2).Ticks / WideRows;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Wide(Noon.AddTicks(i * step), $"region-{random.Next(4)}", $"host-{random.Next(hosts)}", random.NextInt64(1_000_000));
        }

        string plain = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(plain))
        {
            await writer.WriteAsync<Wide>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using VortexFile file = await VortexFile.OpenAsync(plain, cancellationToken: Ct);
        DType schema = file.DType;
        int instant = schema.IndexOfField(nameof(Wide.At));
        long micros = Noon.ToUnixTimeMilliseconds() * 1000;
        long width = TimeSpan.FromHours(2).Ticks / 10 / WideSlices;
        int sliceRows = WideRows / WideSlices;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, WideSlices).Select(s => new TimeSlice(micros + (s * width), micros + ((s + 1) * width), (long)s * sliceRows, sliceRows))],
            [schema.IndexOfField(nameof(Wide.Region)), schema.IndexOfField(nameof(Wide.Host))]);

        string path = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.WithBudgetPerMille(10_000),
            SynopsisColumns = [nameof(Wide.Region), nameof(Wide.Host)],
            ScratchMemoryBytes = 0,
            ScratchDirectory = scratch,
        };

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path, options))
        {
            using SortedSlices slices = new SortedSlices(table, instant, schema);
            using SynopsisTables tables = new SynopsisTables(schema, table.Unit, SynopsisTables.Measures(schema, instant), writer, slices: table.Slices)
            {
                HeldSketchBytes = heldSketchBytes ?? SynopsisTables.DefaultHeldSketchBytes,
            };
            SliceWriter writes = new SliceWriter(writer, tables, null, null);
            int next = 0;
            await foreach (RecordBatch batch in file.Scan<Wide>().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await slices.AppendAsync(batch, (sorted, token) => writes.WriteAsync(sorted, table.Slices[next++].Start, slices.Order, slices.CountedPaths, token), Ct);
                }
            }

            await slices.DrainAsync();
            Assert.Equal(WideSlices, next);
            writer.DeclareSlices(nameof(Wide.At), slices.Table);
            writer.DeclareSynopsisTables(await tables.FinishAsync(Ct));
            WriteReport report = await writer.CompleteAsync(Ct);
            reports?.Add(report);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Wide(DateTimeOffset At, string Region, string Host, long Duration);
}
