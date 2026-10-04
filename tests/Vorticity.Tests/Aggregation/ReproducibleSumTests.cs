using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A float sum, and the mean made of it, is the same bits at every degree of parallelism, every
/// size of batch, in every order of the rows, and over the same rows cut into the objects of a
/// dataset: the indexed sum's, which no plain sum is.
/// </summary>
public sealed partial class ReproducibleSumTests
{
    private const int Rows = 120_000;

    private static readonly string[] Desks = ["rates", "fx", "credit", "equity"];

    [Fact]
    public async Task AFloatSumIsTheSameBitsAtEveryDegreeEveryCutAndEveryOrder()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows, "ordered");
        Trade[] shuffled = (Trade[])rows.Clone();
        new Random(5).Shuffle(shuffled);
        string shuffledPath = await WriteAsync(shuffled, "shuffled");
        try
        {
            Answers expected = Reference(rows);
            foreach (int degree in new[] { 1, 2, 5 })
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
                foreach (string file in new[] { path, shuffledPath })
                {
                    await using VortexFile open = await session.OpenAsync(file, cancellationToken: Ct);
                    foreach (int batch in new[] { 0, 1_000, 4_099 })
                    {
                        Assert.Equal(expected, await AnswersAsync(() => batch == 0 ? open.Scan<Trade>() : open.Scan<Trade>().With(new ScanOptions { BatchRows = batch })));
                    }
                }
            }

            // The same rows as the objects of a dataset, cut where the file's chunks are not.
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(
                store, VortexTypes.ToDType(Trade.Schema, new Vorticity.Types.DTypeArena()), new DatasetOptions { Seed = 11 }, Ct);
            await using (VortexFile open = await VortexFile.OpenAsync(shuffledPath, Ct))
            {
                foreach ((long start, long end) in new[] { (0L, 7_777L), (7_777L, 64_001L), (64_001L, (long)Rows) })
                {
                    await dataset.AppendAsync(open.Scan<Trade>().Rows(new RowRange(start, end)).ToBatchesAsync(Ct), Ct);
                }
            }

            Assert.Equal(3, dataset.ObjectCount);
            Assert.Equal(expected, await AnswersAsync(() => dataset.Scan<Trade>()));
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(shuffledPath);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What every reading must give: the sums and means of the whole and of each desk, as bits.</summary>
    private static async Task<Answers> AnswersAsync(Func<Scan<Trade>> scan)
    {
        double sum = await scan().SumAsync(r => r.Price, Ct);
        double? mean = await scan().AverageAsync(r => r.Size, Ct);
        SortedDictionary<string, (long, long)> desks = new SortedDictionary<string, (long, long)>(StringComparer.Ordinal);
        await foreach (DeskTotals desk in scan().GroupBy(r => r.Desk).Select(g => (g.Key, g.Sum(x => x.Price), g.Average(x => x.Size))).As<DeskTotals>().ToRecordsAsync(Ct))
        {
            desks[desk.Desk] = (BitConverter.DoubleToInt64Bits(desk.Sum), BitConverter.DoubleToInt64Bits(desk.Mean!.Value));
        }

        return new Answers(BitConverter.DoubleToInt64Bits(sum), BitConverter.DoubleToInt64Bits(mean!.Value), string.Join(";", desks.Select(d => $"{d.Key}={d.Value.Item1:X}/{d.Value.Item2:X}")));
    }

    /// <summary>The same answers from the indexed sum over the values themselves.</summary>
    private static Answers Reference(Trade[] rows)
    {
        IndexedSum sum = default;
        IndexedSum size = default;
        SortedDictionary<string, (IndexedSum Sum, IndexedSum Size)> desks = new SortedDictionary<string, (IndexedSum, IndexedSum)>(StringComparer.Ordinal);
        foreach (Trade row in rows)
        {
            (IndexedSum Sum, IndexedSum Size) desk = desks.GetValueOrDefault(row.Desk);
            if (row.Price is double price)
            {
                sum.Add(price);
                desk.Sum.Add(price);
            }

            size.Add(row.Size);
            desk.Size.Add(row.Size);
            desks[row.Desk] = desk;
        }

        return new Answers(
            BitConverter.DoubleToInt64Bits(sum.Value),
            BitConverter.DoubleToInt64Bits(size.Value / size.Count),
            string.Join(";", desks.Select(d => $"{d.Key}={BitConverter.DoubleToInt64Bits(d.Value.Sum.Value):X}/{BitConverter.DoubleToInt64Bits(d.Value.Size.Value / d.Value.Size.Count):X}")));
    }

    /// <summary>Prices over forty binades, of both signs, with nulls and NaN; sizes as singles.</summary>
    private static Trade[] Trades()
    {
        Random random = new Random(29);
        Trade[] rows = new Trade[Rows];
        for (int row = 0; row < Rows; row++)
        {
            double? price = row % 31 == 0 ? null
                : row % 97 == 0 ? double.NaN
                : Math.ScaleB(random.NextDouble() + 0.5, random.Next(-20, 21)) * (random.Next(3) == 0 ? -1 : 1);
            rows[row] = new Trade(Desks[row % 7 % Desks.Length], price, (float)(random.NextDouble() * 1e4));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Trade[] rows, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "reproducible-sums");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Trade>(path))
        {
            await writer.WriteAsync<Trade>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private sealed record Answers(long Sum, long Mean, string Desks);

    [VortexRecord]
    public partial record struct Trade(string Desk, double? Price, float Size);

    [VortexRecord]
    public partial record struct DeskTotals(string Desk, double Sum, double? Mean);
}
