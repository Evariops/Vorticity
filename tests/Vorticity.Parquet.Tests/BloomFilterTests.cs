using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The Bloom filters the writer gives the columns it is asked to, and the row groups the reader
/// rules out by them: a value absent from every row group read nowhere, where the statistics, every
/// group spanning every value, rule out nothing.
/// </summary>
public sealed partial class BloomFilterTests : IDisposable
{
    private const int Rows = 64 * 1_024;
    private const int GroupRows = 8 * 1_024;
    private const int PageRows = 1_024;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-bloom-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Sample(long Key, string Code, double Score, sbyte Small, decimal Price, string Mixed);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task RulesOutTheRowGroupsAValueIsAbsentFrom()
    {
        Sample[] rows = await WriteAsync(new Dictionary<string, double> { ["Key"] = 0.01, ["Code"] = 0.01, ["Score"] = 0.01, ["Small"] = 0.01, ["Price"] = 0.01, ["Mixed"] = 0.01 });
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        int blocks = Rows / GroupRows;

        // A key no row holds, inside every group's bounds: every filter says it is absent.
        long missing = 500_000_000;
        while (rows.Any(r => r.Key == missing))
        {
            missing++;
        }

        Scan<Sample> absent = file.Scan<Sample>().Where(s => s.Key == missing);
        Assert.Equal(0, await absent.CountAsync(Ct));
        Assert.True(absent.Metrics.BlocksPruned >= blocks - 1, $"{absent.Metrics.BlocksPruned} of {blocks} pruned");
        ScanPlan plan = await file.Scan<Sample>().Where(s => s.Key == missing).ExplainAsync(Ct);
        Assert.Equal(0, plan.Pruning.Single(step => step.Structure == "row group statistics").BlocksPruned);
        Assert.True(plan.Pruning.Single(step => step.Structure == "bloom filter").BlocksPruned >= blocks - 1);

        // A key one row holds: its group is read, and the row found.
        long present = rows[40_000].Key;
        Assert.Equal(rows.Count(r => r.Key == present), await file.Scan<Sample>().Where(s => s.Key == present).CountAsync(Ct));
        long other = rows[5].Key;
        Assert.Equal(rows.Count(r => r.Key == present || r.Key == other || r.Key == 77), await file.Scan<Sample>().Where(s => s.Key.In(present, other, 77)).CountAsync(Ct));
        string code = rows[123].Code;
        Assert.Equal(rows.Count(r => r.Code == code), await file.Scan<Sample>().Where(s => s.Code == code).CountAsync(Ct));
        Assert.Equal(0, await file.Scan<Sample>().Where(s => s.Code == "code-5G").CountAsync(Ct));

        // A zero is probed as both zeros: the negative one is found by an equality to the positive.
        Assert.Equal(1, await file.Scan<Sample>().Where(s => s.Score == 0.0).CountAsync(Ct));
        Assert.Equal(1, await file.Scan<Sample>().Where(s => s.Score == -0.0).CountAsync(Ct));

        // A narrowed integer is hashed at its physical width, a decimal as its unscaled bytes.
        Assert.Equal(rows.Count(r => r.Small == 7), await file.Scan<Sample>().Where(s => s.Small == 7).CountAsync(Ct));
        decimal price = rows[999].Price;
        Assert.Equal(1, await file.Scan<Sample>().Where(s => s.Price == price).CountAsync(Ct));

        // A chunk whose dictionary gave out: the entries of its pages of codes and the values of its
        // PLAIN pages after them found, and a value of neither, inside the bounds, ruled out.
        for (int group = 0; group < blocks; group++)
        {
            ColumnChunkMetadata chunk = file.Footer.Chunk(group, 5);
            Assert.True(chunk.DictionaryPageOffset >= 0 && chunk.HasEncodingStats && !chunk.AllDataPagesDictionary);
        }

        Assert.Equal(rows.Count(r => r.Mixed == "m-3"), await file.Scan<Sample>().Where(s => s.Mixed == "m-3").CountAsync(Ct));
        foreach (int page in (int[])[5, 7])
        {
            string unique = rows[(3 * GroupRows) + (page * PageRows) + 7].Mixed;
            Assert.Equal(1, await file.Scan<Sample>().Where(s => s.Mixed == unique).CountAsync(Ct));
        }

        ScanPlan neither = await file.Scan<Sample>().Where(s => s.Mixed == "q").ExplainAsync(Ct);
        Assert.Equal(0, neither.Pruning.Single(step => step.Structure == "row group statistics").BlocksPruned);
        Assert.True(neither.Pruning.Single(step => step.Structure == "bloom filter").BlocksPruned >= blocks - 1);

        // Pruning or not, the same rows.
        Func<Probe<Sample>, Predicate>[] filters =
        [
            s => s.Key == present | s.Key == 3,
            s => s.Code.In(rows[1].Code, rows[60_000].Code, "absent"),
            s => s.Key == missing & s.Small == 3,
            s => s.Key == missing | s.Code == "code-5G",
            s => s.Score == 0.0 | s.Price == 1.23m,
            s => !(s.Key == present),
        ];
        foreach (Func<Probe<Sample>, Predicate> filter in filters)
        {
            long pruned = await file.Scan<Sample>().Where(filter).CountAsync(Ct);
            long whole = await file.Scan<Sample>().Where(filter).With(new ScanOptions { UseIndexes = false, UseStatistics = false }).CountAsync(Ct);
            Assert.Equal(whole, pruned);
        }
    }

    /// <summary>
    /// The suite's filter of "hello", "parquet", "bloom" and "filter", which parquet-mr wrote: its
    /// header read, and the four found by the hash and the blocks this library's filters use.
    /// </summary>
    [Fact]
    public void FindsTheValuesOfAFilterAnotherWriterWrote()
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");
        Assert.SkipWhen(root is null || !Directory.Exists(root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        byte[] bytes = System.IO.File.ReadAllBytes(Path.Combine(root!, "data/bloom_filter.xxhash.bin"));

        Assert.True(BloomFilterHeader.TryRead(bytes, out int header, out int bitset));
        Assert.Equal(bytes.Length, header + bitset);
        ReadOnlySpan<uint> words = Indexes.SplitBlockBloom.Words(bytes.AsSpan(header, bitset));
        foreach (string value in (string[])["hello", "parquet", "bloom", "filter"])
        {
            Assert.True(Indexes.SplitBlockBloom.Contains(words, Indexes.SplitBlockBloom.Hash(System.Text.Encoding.UTF8.GetBytes(value), Indexes.BloomHash.XxHash64)), value);
        }

        int found = 0;
        for (int i = 0; i < 1_000; i++)
        {
            found += Indexes.SplitBlockBloom.Contains(words, Indexes.SplitBlockBloom.Hash(System.Text.Encoding.UTF8.GetBytes($"absent-{i}"), Indexes.BloomHash.XxHash64)) ? 1 : 0;
        }

        Assert.True(found < 10, $"{found} of 1000 absent values found");

        // The older filter, of a binary header and Murmur3, is not one this library reads.
        Assert.False(BloomFilterHeader.TryRead(System.IO.File.ReadAllBytes(Path.Combine(root!, "data/bloom_filter.bin")), out _, out _));
    }

    /// <summary>
    /// The suite's files of a string column parquet-mr gave a Bloom filter, one with its length and one
    /// without: every value it holds found, and every value just past one, inside the bounds, ruled out.
    /// </summary>
    [Theory]
    [InlineData("data/data_index_bloom_encoding_stats.parquet")]
    [InlineData("data/data_index_bloom_encoding_with_length.parquet")]
    public async Task RulesOutByTheFiltersOfAnotherWriter(string relative)
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");
        Assert.SkipWhen(root is null || !Directory.Exists(root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        await using ParquetFile file = await ParquetFile.OpenAsync(Path.Combine(root!, relative), Ct);
        List<string> values = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                int node = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(0);
                for (int r = 0; r < batch.RowCount; r++)
                {
                    if (Compute.LiteralReader.TryRead(batch.Arena, node, r, out Expressions.FilterLiteral value))
                    {
                        values.Add(System.Text.Encoding.UTF8.GetString(value.BytesValue));
                    }
                }
            }
        }

        values = [.. values.Distinct().Order(StringComparer.Ordinal)];
        Assert.True(values.Count > 2);
        int ruledOut = 0;
        for (int i = 0; i < values.Count; i++)
        {
            string present = values[i];
            Assert.Equal(1, (await file.Scan().Where(Expressions.Expr.Eq(Expressions.Expr.Field("String"), Expressions.Expr.Literal(Expressions.FilterLiteral.From(present)))).ExplainAsync(Ct)).LiveBlocks);
            if (i < values.Count - 1)
            {
                VortexExpr absent = Expressions.Expr.Eq(Expressions.Expr.Field("String"), Expressions.Expr.Literal(Expressions.FilterLiteral.From(present + "\0")));
                ScanPlan plan = await file.Scan().Where(absent).ExplainAsync(Ct);
                Assert.Equal(0, plan.Pruning.Single(step => step.Structure == "row group statistics").BlocksPruned);
                ruledOut += plan.Pruning.Single(step => step.Structure == "bloom filter").BlocksPruned;
                Assert.Equal(0, await file.Scan().Where(absent).CountAsync(Ct));
            }
        }

        Assert.Equal(values.Count - 1, ruledOut);
    }

    /// <summary>
    /// A footer that points a chunk's Bloom filter past the file, or at bytes that are no filter:
    /// the filter is done without and the rows read, where reading it would have failed the scan.
    /// </summary>
    [Theory]
    [InlineData(1L << 40, 100)]
    [InlineData(1L << 40, 0)]
    [InlineData(4L, 0)]
    [InlineData(4L, 40)]
    [InlineData(4L, int.MaxValue)]
    public async Task DoesWithoutAFilterThatIsNone(long offset, int length)
    {
        HandBuiltFile built = new HandBuiltFile(1) { Rows = 3 }.Leaf("Id", FieldRepetition.Required, PhysicalType.Int64);
        HandBuiltFile.Column column = built.Chunk(PhysicalType.Int64, "Id").V2(null, 0, [0, 0, 0], 0, 3, HandBuiltFile.Longs(5, 6, 7));
        column.BloomFilterOffset = offset;
        column.BloomFilterLength = length;
        await System.IO.File.WriteAllBytesAsync(_path, built.ToBytes(), Ct);

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        VortexExpr six = Expressions.Expr.Eq(Expressions.Expr.Field("Id"), Expressions.Expr.Literal(Expressions.FilterLiteral.From(6L)));
        Assert.Equal(1, await file.Scan().Where(six).CountAsync(Ct));
        Assert.Equal(0, (await file.Scan().Where(six).ExplainAsync(Ct)).Pruning.Sum(step => step.BlocksPruned));
    }

    [Fact]
    public async Task RefusesAFilterOfNoColumnOrOfNoRate()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(new Dictionary<string, double> { ["Nope"] = 0.01 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WriteAsync(new Dictionary<string, double> { ["Key"] = 1.5 }));
    }

    private async Task<Sample[]> WriteAsync(IReadOnlyDictionary<string, double> blooms)
    {
        Random random = new(3);
        Sample[] rows = new Sample[Rows];
        for (int i = 0; i < Rows; i++)
        {
            long key = random.NextInt64(0, 1_000_000_000);
            // Half a row group of ten values, then values each its own and long enough that the
            // dictionary passes its bound two pages in.
            string mixed = i % GroupRows < GroupRows / 2 ? $"m-{i % 10}" : $"u-{i:D6}-{new string('x', 400)}";
            rows[i] = new Sample(key, $"code-{key:X}", i == 777 ? -0.0 : i + 0.5, (sbyte)(i % 200 - 100), (i * 7) * 0.01m, mixed);
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Sample>(
            _path, new ParquetWriteOptions { RowGroupRows = GroupRows, BlockRows = PageRows, BloomFilters = blooms });
        await writer.WriteAsync<Sample>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return rows;
    }
}
