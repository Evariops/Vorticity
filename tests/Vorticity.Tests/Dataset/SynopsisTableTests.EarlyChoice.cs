// Synopsis tables given up as they grow: a table its rows rule out against the object's budget is given up before the
// end, with its reason in the write's report, and the choice keeps what it would have kept had every table been built.
// Every table kept holds what the rows give, its chunks read one after another, its runs waiting on disk.
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
    private const int SpreadRows = 60_000;

    private const int SpreadSlices = 30;

    /// <summary>
    /// Three hours of 60 000 rows in 30 slices, sorted by a region (4 values), a status (5) and a request drawn from
    /// 24 000, almost unique to each row with the two before it, a duration as the measure; every run waits on disk.
    /// With the early verdicts from 32 KiB of data, the tables of the three columns, as large as the data, and the hours'
    /// value counts of the request are given up as they grow, each with its reason, while the choice keeps the same
    /// tables as when every one is built to the end, and each holds what the rows give.
    /// </summary>
    [Fact]
    public async Task TablesTheRowsRuleOutAreGivenUpAsTheyGrowAndTheChoiceStands()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-early-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            (string early, WriteReport earlyReport) = await SpreadAsync(scratch, budgetPerMille: 100, earlyChoiceBytes: 32 << 10);
            (string late, WriteReport lateReport) = await SpreadAsync(scratch, budgetPerMille: 100, earlyChoiceBytes: IndexWriter.DefaultEarlyChoiceBytes);
            List<IndexWriteReport> earlyTables = [.. earlyReport.Indexes.Where(r => r.Kind == IndexKinds.SynopsisTable)];
            List<IndexWriteReport> lateTables = [.. lateReport.Indexes.Where(r => r.Kind == IndexKinds.SynopsisTable)];

            // Every grain is weighed either way, those given up among them; only the reasons differ.
            Assert.Equal(lateTables.Select(t => t.Column), earlyTables.Select(t => t.Column));
            foreach (string grain in (string[])[
                "Coarse per Hour of (Region, Status, Request)",
                "Coarse per Object of (Region, Status, Request)",
                "Runs per Slice of (Region, Status, Request)",
                "Values per Hour of (Request)"])
            {
                IndexWriteReport table = Assert.Single(earlyTables, t => t.Column == grain);
                Assert.Equal(IndexOutcome.Abandoned, table.Outcome);
                Assert.True(
                    table.Reason!.StartsWith("given up as", StringComparison.Ordinal),
                    $"{grain} was not given up early:\n{string.Join('\n', earlyTables.Select(t => $"  {t.Column}: {t.Reason}"))}");
                Assert.DoesNotContain("given up as", Assert.Single(lateTables, t => t.Column == grain).Reason!, StringComparison.Ordinal);
            }

            Assert.Equal(
                lateTables.Where(t => t.Outcome == IndexOutcome.Built).Select(t => (t.Column, t.Bytes)),
                earlyTables.Where(t => t.Outcome == IndexOutcome.Built).Select(t => (t.Column, t.Bytes)));
            Assert.Contains(earlyTables, t => t.Outcome == IndexOutcome.Built);
            await SpreadCountsExactlyAsync(early);
            await SpreadCountsExactlyAsync(late);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The same rows, the tables cut into chunks of 64 rows after a first of 4, and an hour's rows into chunks of their
    /// own past 4 KiB: every table kept, read chunk after chunk, holds what the rows give.
    /// </summary>
    [Fact]
    public async Task ATableOfManyChunksHoldsWhatTheRowsGive()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-chunks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            (string path, WriteReport _) = await SpreadAsync(scratch, budgetPerMille: 100, earlyChoiceBytes: IndexWriter.DefaultEarlyChoiceBytes, chunking: (64, 4 << 10));
            List<(SynopsisTableInfo Info, List<TableRow> Rows, int Chunks)> tables = await SpreadCountsExactlyAsync(path);
            Assert.True(tables.Count(t => t.Chunks > 1) >= 4, string.Join(", ", tables.Select(t => $"{t.Info.Kind} {t.Info.Bucket} {t.Info.Keys.Length}: {t.Chunks}")));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The spread rows written in sorted slices with their synopsis, every run of the tables on disk, under the budget
    /// <paramref name="budgetPerMille"/> and early verdicts from <paramref name="earlyChoiceBytes"/> of data.
    /// </summary>
    private static async Task<(string Path, WriteReport Report)> SpreadAsync(
        string scratch, int budgetPerMille, long earlyChoiceBytes, (int Rows, long HourBytes)? chunking = null)
    {
        Random random = new Random(11);
        Spread[] rows = new Spread[SpreadRows];
        long step = TimeSpan.FromHours(3).Ticks / SpreadRows;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Spread(Noon.AddTicks(i * step), $"region-{random.Next(4)}", 200 + (random.Next(5) * 100), random.Next(SpreadRows * 2 / 5), random.NextInt64(1_000_000));
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
        long width = TimeSpan.FromHours(3).Ticks / 10 / SpreadSlices;
        int sliceRows = SpreadRows / SpreadSlices;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, SpreadSlices).Select(s => new TimeSlice(micros + (s * width), micros + ((s + 1) * width), (long)s * sliceRows, sliceRows))],
            [schema.IndexOfField(nameof(Spread.Region)), schema.IndexOfField(nameof(Spread.Status)), schema.IndexOfField(nameof(Spread.Request))]);

        string path = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.WithBudgetPerMille(budgetPerMille),
            SynopsisColumns = [nameof(Spread.Region), nameof(Spread.Status), nameof(Spread.Request)],
            SynopsisEarlyChoiceBytes = earlyChoiceBytes,
            ScratchMemoryBytes = 0,
            ScratchDirectory = scratch,
            ChunkTargetBytes = 16 << 10,
        };

        WriteReport report;
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Spread>(path, options))
        {
            using SortedSlices slices = new SortedSlices(table, instant, schema);
            using SynopsisTables tables = new SynopsisTables(
                schema, table.Unit, SynopsisTables.Measures(schema, instant, table.Order), writer, slices: table.Slices, chunking: chunking);
            int next = 0;
            await foreach (RecordBatch batch in file.Scan<Spread>().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await slices.AppendAsync(batch, async (sorted, token) =>
                    {
                        await tables.AddSliceAsync(sorted.Batch, table.Slices[next++].Start, slices.Order, sorted.Codes, cancellationToken: token);
                        await writer.WriteBatchAsync(sorted.Batch, token);
                    }, Ct);
                }
            }

            await slices.DrainAsync();
            Assert.Equal(SpreadSlices, next);
            writer.DeclareSlices(nameof(Spread.At), slices.Table);
            writer.DeclareSynopsisTables(await tables.FinishAsync(Ct));
            report = await writer.CompleteAsync(Ct);
        }

        return (path, report);
    }

    /// <summary>
    /// The tables of the spread file at <paramref name="path"/>, each read chunk after chunk: every row what the rows it
    /// covers give, in the order of its bucket then its keys.
    /// </summary>
    private static async Task<List<(SynopsisTableInfo Info, List<TableRow> Rows, int Chunks)>> SpreadCountsExactlyAsync(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
        SliceTable slices = (await SliceTable.ReadAsync(file, Ct))!;
        DType dtype = file.DType;
        int[] keys = [.. slices.Order];
        int[] measures = [dtype.IndexOfField(nameof(Spread.Duration))];
        Kind[] kinds = Kinds(dtype, measures);
        List<object?[]> fields = [];
        await foreach (Spread row in file.Scan<Spread>().ToRecordsAsync(Ct))
        {
            object?[] values = new object?[dtype.FieldCount];
            values[dtype.IndexOfField(nameof(Spread.Region))] = row.Region;
            values[dtype.IndexOfField(nameof(Spread.Status))] = (long)row.Status;
            values[dtype.IndexOfField(nameof(Spread.Request))] = row.Request;
            values[dtype.IndexOfField(nameof(Spread.Duration))] = row.Duration;
            fields.Add(values);
        }

        List<long> starts = [];
        foreach (TimeSlice slice in slices.Slices)
        {
            starts.AddRange(Enumerable.Repeat(slice.Start, (int)slice.Rows));
        }

        // A table's chunks one after another, in the order the file lists them.
        List<(SynopsisTableInfo Info, List<TableRow> Rows, int Chunks)> tables = [];
        foreach ((SynopsisTableInfo info, List<TableRow> rows) in await TablesAsync(file))
        {
            int at = tables.FindIndex(t => t.Info.Kind == info.Kind && t.Info.Bucket == info.Bucket && t.Info.Keys.SequenceEqual(info.Keys));
            if (at < 0)
            {
                tables.Add((info, [.. rows], 1));
                continue;
            }

            tables[at].Rows.AddRange(rows);
            tables[at] = tables[at] with { Chunks = tables[at].Chunks + 1 };
        }

        Assert.NotEmpty(tables);
        foreach ((SynopsisTableInfo info, List<TableRow> rows, int _) in tables)
        {
            Assert.Equal(measures, info.Measures);
            Assert.Equal(info.Rows, rows.Count);
            List<TableRow> units = [];
            for (int row = 0; row < fields.Count; row++)
            {
                long bucket = info.Bucket switch
                {
                    SynopsisBucket.Slice => starts[row],
                    SynopsisBucket.Hour => TimeSlicer.Floor(starts[row], slices.Unit, SliceWidth.Hour),
                    _ => 0,
                };
                units.Add(Unit(bucket, [.. info.Keys.Select(k => fields[row][k])], [.. measures.Select(m => fields[row][m])], kinds));
            }

            Assert.Equal(info.Kind == SynopsisTableKind.Values ? [info.Keys[0]] : keys[..info.Keys.Length], info.Keys);
            List<TableRow> expected = Merge(units, static row => row.Bucket, [.. Enumerable.Range(0, info.Keys.Length)], kinds);
            Assert.Equal(Show(expected, kinds), Show(rows, kinds));
        }

        return tables;
    }

    [VortexRecord]
    public partial record struct Spread(DateTimeOffset At, string Region, int Status, long Request, long Duration);
}
