using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Row groups ruled out by the dictionaries of the columns a filter reads: where the statistics,
/// every group's bounds spanning the value asked, rule out nothing, the filter evaluated over each
/// chunk's distinct values proves none of its rows selected.
/// </summary>
public sealed partial class DictionaryPruningTests : IDisposable
{
    private const int Rows = 64 * 1_024;
    private const int GroupRows = 8 * 1_024;
    private const int Groups = Rows / GroupRows;

    private static readonly string[] Even = ["apple", "cherry", "plum"];
    private static readonly string[] Odd = ["banana", "kiwi", "pear"];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-dictionary-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Fruit(long Id, string Tag, string? Note, int Weight);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task RulesOutTheGroupsNoneOfWhoseValuesMatch()
    {
        Fruit[] rows = await WriteAsync();
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);

        // Inside the bounds of every group, in the odd ones alone.
        await AssertPrunedAsync(file, rows, s => s.Tag == "kiwi", r => r.Tag == "kiwi", Groups / 2);

        // In none.
        await AssertPrunedAsync(file, rows, s => s.Tag == "grape", r => r.Tag == "grape", Groups);
        await AssertPrunedAsync(file, rows, s => s.Tag.In("grape", "lime"), r => r.Tag is "grape" or "lime", Groups);

        // Patterns the bounds know nothing of.
        await AssertPrunedAsync(file, rows, s => s.Tag.StartsWith("b"), r => r.Tag.StartsWith('b'), Groups / 2);
        await AssertPrunedAsync(file, rows, s => s.Tag.Contains("rr"), r => r.Tag.Contains("rr", StringComparison.Ordinal), Groups / 2);
        await AssertPrunedAsync(file, rows, s => s.Tag.Like("%zz%"), r => false, Groups);

        // A negation the dictionary decides: every value of the even groups is in the set.
        await AssertPrunedAsync(file, rows, s => !s.Tag.In("apple", "cherry", "plum"), r => !Even.Contains(r.Tag), Groups / 2);

        // Across columns: the side a dictionary rules out rules the conjunction out.
        await AssertPrunedAsync(file, rows, s => s.Tag == "kiwi" & s.Weight == 15, r => r.Tag == "kiwi" && r.Weight == 15, Groups / 2);

        // An integer's dictionary, inside its bounds.
        await AssertPrunedAsync(file, rows, s => s.Weight == 25, r => r.Weight == 25, Groups);

        // A column that holds nulls: a null row selects no comparison, and a null check stays open.
        await AssertPrunedAsync(file, rows, s => s.Note == "n-2x", r => r.Note == "n-2x", Groups);
        await AssertPrunedAsync(file, rows, s => s.Note.IsNull, r => r.Note is null, 0);
        await AssertPrunedAsync(file, rows, s => !(s.Note == "n-1"), r => r.Note is not null && r.Note != "n-1", 0);
    }

    [Fact]
    public async Task CountsTheSameRowsPrunedOrNot()
    {
        Fruit[] rows = await WriteAsync();
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Func<Probe<Fruit>, Predicate>[] filters =
        [
            s => s.Tag == "kiwi" | s.Id == 3,
            s => !(s.Tag == "kiwi") & s.Weight > 10,
            s => !(s.Tag == "kiwi" | s.Tag == "pear"),
            s => s.Tag >= "c" & s.Tag < "l",
            s => s.Note.IsNull | s.Tag == "plum",
            s => !s.Note.IsNull & s.Note.StartsWith("n-3"),
            s => s.Weight.In(10, 15, 99),
            s => !(s.Weight == 10 | s.Weight == 20 | s.Weight == 30),
        ];
        foreach (Func<Probe<Fruit>, Predicate> filter in filters)
        {
            long pruned = await file.Scan<Fruit>().Where(filter).CountAsync(Ct);
            long whole = await file.Scan<Fruit>().Where(filter).With(new ScanOptions { UseStatistics = false }).CountAsync(Ct);
            Assert.Equal(whole, pruned);
            Assert.Equal(whole, await ReadCountAsync(file.Scan<Fruit>().Where(filter)));
        }
    }

    private static async Task AssertPrunedAsync(ParquetFile file, Fruit[] rows, Func<Probe<Fruit>, Predicate> filter, Func<Fruit, bool> selects, int groups)
    {
        ScanPlan plan = await file.Scan<Fruit>().Where(filter).ExplainAsync(Ct);
        Assert.Equal(groups, plan.Pruning.Single(step => step.Structure == "dictionary").BlocksPruned);
        Assert.Equal(rows.Count(selects), await ReadCountAsync(file.Scan<Fruit>().Where(filter)));
        Assert.Equal(rows.Count(selects), await file.Scan<Fruit>().Where(filter).CountAsync(Ct));
    }

    /// <summary>The rows a scan reads, counted from its batches rather than by a count it may prove.</summary>
    private static async Task<long> ReadCountAsync(Scan<Fruit> scan)
    {
        long count = 0;
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            using (batch)
            {
                count += batch.RowCount;
            }
        }

        return count;
    }

    private async Task<Fruit[]> WriteAsync()
    {
        Fruit[] rows = new Fruit[Rows];
        for (int i = 0; i < Rows; i++)
        {
            bool even = i / GroupRows % 2 == 0;
            string tag = (even ? Even : Odd)[i % 3];
            string? note = i % 5 == 0 ? null : $"n-{i % 4}";
            int weight = even ? 10 * (1 + (i % 3)) : (i % 2 == 0 ? 15 : 35);
            rows[i] = new Fruit(i, tag, note, weight);
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Fruit>(
            _path, new ParquetWriteOptions { RowGroupRows = GroupRows, BlockRows = 1_024 });
        await writer.WriteAsync<Fruit>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return rows;
    }
}
