using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.IO;

/// <summary>
/// A session that does not map the files it opens from a path: its reads are positional, so a file
/// cut short under a scan fails a read with an exception, where a mapping would kill the process.
/// </summary>
public sealed class MapFilesTests
{
    /// <summary>Rows per write, a chunk each: the cut must fall on chunks the scan has not read yet.</summary>
    private const int ChunkRows = 1 << 17;

    private const int Chunks = 16;

    [Fact]
    public async Task AFileCutShortUnderAScanFailsTheReadNotTheProcess()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a reader's share mode keeps a writer out of the file");
        string path = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MapFiles = false);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            int batches = 0;
            await Assert.ThrowsAsync<VortexFormatException>(async () =>
            {
                await foreach (BatchView batch in file.Scan("id").With(new ScanOptions { Prefetch = 0 }).WithCancellation(TestContext.Current.CancellationToken))
                {
                    if (++batches == 1)
                    {
                        // What another process may do to a file it does not know is being read.
                        using FileStream cut = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        cut.SetLength(4_096);
                    }
                }
            });

            Assert.InRange(batches, 1, Chunks - 1);
        }
        finally
        {
            global::System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileOpenedFromAPathIsReadPositionallyAndWhole()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MapFiles = false);
            long sum = 0;
            await using (VortexFile file = await session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.IsType<FileSegmentSource>(SessionReader.Unwrap(file.Source));
                await foreach (BatchView batch in file.Scan("id").WithCancellation(TestContext.Current.CancellationToken))
                {
                    foreach (long value in batch.Column<long>("id").Values)
                    {
                        sum += value;
                    }
                }
            }

            Assert.Equal(Sum(), sum);
            Assert.Null(session.Mappings);
        }
        finally
        {
            global::System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void TheDefaultSessionMapsFiles()
    {
        Assert.True(VortexSession.Default.Options.MapFiles);
    }

    /// <summary>The seed of the values: random ones, which no encoding shrinks, so that each chunk keeps its size on disk.</summary>
    private const int Seed = 3;

    private static long Sum()
    {
        Random random = new Random(Seed);
        long sum = 0;
        for (int row = 0; row < Chunks * ChunkRows; row++)
        {
            sum += random.NextInt64();
        }

        return sum;
    }

    /// <summary>A file of <see cref="Chunks"/> chunks of one 64-bit column, under a name of its own.</summary>
    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "map-files");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"ids-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        VortexSchema schema = [("id", VortexType.Int64)];
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter(path, schema, new VortexWriteOptions { ChunkTargetBytes = ChunkRows * sizeof(long) });
        Random random = new Random(Seed);
        long[] ids = new long[ChunkRows];
        for (int chunk = 0; chunk < Chunks; chunk++)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = random.NextInt64();
            }

            ColumnsBuilder builder = writer.Builder();
            builder.Column<long>(0).Append(ids);
            await writer.WriteAsync(builder, CancellationToken.None);
            await writer.FlushAsync(CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }
}
