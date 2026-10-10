// A short, deterministic Parquet fuzz campaign inside the normal suite.
//
// The real campaign runs in the fuzzer, tests/Vorticity.Fuzz --parquet, over the standard's suite
// and this writer's seeds; a few hundred of its mutations run here with a FIXED SEED, cheap enough
// to ignore and enough to catch the change that breaks every mutated file at once. The mutations are
// the fuzzer's own, its source compiled into this assembly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Fuzz;
using Xunit;

namespace Vorticity.Parquet.Tests;

public sealed partial class FuzzSmokeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vx-parquet-smoke-{Guid.NewGuid():N}");

    [VortexRecord]
    public partial record struct Row(long Id, int? Small, double Measure, string Label, string? Note, bool Flag, decimal Price, int[] Tags);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A mapping a session still caches lets its file go with the session.
        }
    }

    /// <summary>The invariant: only a format or unsupported exception may escape a mutated file, read mapped or by positional reads, or verified.</summary>
    [Fact]
    public async Task MutatedFilesFailCleanlyOrReadWhole()
    {
        Directory.CreateDirectory(_directory);
        List<byte[]> seeds = [];
        foreach ((DataPageVersion pages, ParquetCompression codec) in ((DataPageVersion, ParquetCompression)[])
            [(DataPageVersion.V2, ParquetCompression.Zstd), (DataPageVersion.V1, ParquetCompression.Snappy), (DataPageVersion.V2, ParquetCompression.Uncompressed)])
        {
            seeds.Add(await SeedAsync(pages, codec));
        }

        Random random = new(20261010);
        int deep = 0;
        await using VortexSession positional = VortexSession.Create(options => options.MapFiles = false);
        for (int i = 0; i < 300; i++)
        {
            (byte[] mutated, string what) = ParquetMutator.Apply(seeds[random.Next(seeds.Count)], random, random.Next(ParquetMutator.Kinds));
            string path = Path.Combine(_directory, $"m{i}.parquet");
            await System.IO.File.WriteAllBytesAsync(path, mutated, Ct);
            try
            {
                await using (VortexSession mapped = VortexSession.Create(options => options.MapFiles = true))
                {
                    await using ParquetFile file = await mapped.OpenParquetAsync(path, null, Ct);
                    deep++;
                    await foreach (BatchView batch in file.Scan().WithCancellation(Ct))
                    {
                        _ = batch.RowCount;
                    }
                }

                await using ParquetFile read = await positional.OpenParquetAsync(path, null, Ct);
                await foreach (BatchView batch in read.Scan().WithCancellation(Ct))
                {
                    _ = batch.RowCount;
                }

                _ = await read.VerifyAsync(Ct);
            }
            catch (ParquetFormatException)
            {
            }
            catch (ParquetUnsupportedException)
            {
            }
            catch (Exception error)
            {
                Assert.Fail($"mutation {i} ({what}) escaped as {error.GetType().Name}: {error.Message}");
            }
        }

        // A campaign whose every mutation died at the open proved nothing of the decoders.
        Assert.True(deep * 3 > 300, $"{deep} of 300 mutations opened");
    }

    private async Task<byte[]> SeedAsync(DataPageVersion pages, ParquetCompression codec)
    {
        Random random = new(5);
        Row[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Row(
            i * 3L,
            i % 5 == 0 ? null : random.Next(-1_000, 1_000),
            Math.Round(random.NextDouble() * 1_000, 2),
            $"label-{random.Next(40)}",
            i % 3 == 0 ? null : $"note-{random.Next(10_000)}",
            i % 4 != 0,
            random.Next(-100_000, 100_000) / 100m,
            [.. Enumerable.Range(0, i % 4).Select(t => random.Next(100))]))];
        string path = Path.Combine(_directory, $"seed-{pages}-{codec}.parquet");
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Row>(path, new ParquetWriteOptions
        {
            DataPageVersion = pages,
            Compression = codec,
            BlockRows = 512,
            RowGroupRows = 1_024,
            PageBytes = 2 << 10,
            WriteChecksums = true,
            BloomFilters = new Dictionary<string, double> { ["Id"] = 0.05 },
        }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return await System.IO.File.ReadAllBytesAsync(path, Ct);
    }
}
