// The zstd level a write asks for: the range the options accept, the bytes it changes and the ones
// it leaves alone, and the shared compressors that serve every level.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ManagedCompressions.Zstd;
using Vorticity.File;
using Vorticity.Tests.Scan;
using Vorticity.Writing;

using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class ZstdLevelTests
{
    private const int Rows = 3 * 8_192;

    private static readonly Guid Pinned = new Guid("5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a");

    private static readonly string[] Words =
    [
        "GET", "POST", "/index.html", "/api/v1/items", "?page=", "&sort=desc", "Mozilla/5.0", "Chrome/131.0",
        "200", "404", "gzip", "keep-alive", "text/html", "application/json", "utf-8", "max-age=3600",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheLevelIsOneZstdHas()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexWriteOptions { ZstdLevel = ZstdCompressor.MaxLevel + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexWriteOptions { ZstdLevel = ZstdCompressor.MinLevel - 1 });
        Assert.Equal(0, new VortexWriteOptions().ZstdLevel);
        Assert.Equal(-5, new VortexWriteOptions { ZstdLevel = -5 }.ZstdLevel);
        Assert.Equal(ZstdCompressor.MaxLevel, new VortexWriteOptions { ZstdLevel = ZstdCompressor.MaxLevel }.ZstdLevel);
    }

    [Fact]
    public async Task AColumnZstdTakesIsWrittenAtTheLevelAsked()
    {
        Line[] rows = Lines();
        string fast = await WriteAsync(rows, level: 1, degree: 1);
        string strong = await WriteAsync(rows, level: 19, degree: 1);
        string standard = await WriteAsync(rows, level: 0, degree: 1);
        try
        {
            long fastBytes = new FileInfo(fast).Length;
            long strongBytes = new FileInfo(strong).Length;
            long standardBytes = new FileInfo(standard).Length;
            Assert.True(strongBytes < standardBytes && standardBytes < fastBytes, $"level 19 {strongBytes} B, 3 {standardBytes} B, 1 {fastBytes} B");

            foreach (string path in new[] { fast, strong, standard })
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
                Assert.Equal(rows.Select(r => r.Text), await file.Scan<Line>().Select(r => r.Text).ToListAsync(Ct));
            }
        }
        finally
        {
            System.IO.File.Delete(fast);
            System.IO.File.Delete(strong);
            System.IO.File.Delete(standard);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    public async Task AFileIsTheSameBytesOnAnyNumberOfThreadsAtAnyLevel(int level)
    {
        Line[] rows = Lines();
        string one = await WriteAsync(rows, level, degree: 1);
        string many = await WriteAsync(rows, level, degree: 8);
        try
        {
            Assert.Equal(await System.IO.File.ReadAllBytesAsync(one, Ct), await System.IO.File.ReadAllBytesAsync(many, Ct));
        }
        finally
        {
            System.IO.File.Delete(one);
            System.IO.File.Delete(many);
        }
    }

    [Fact]
    public void TheSharedCompressorsServeTheLevelAsked()
    {
        ZstdCompressor fast = ZstdEncoders.Rent(1);
        ZstdCompressor standard = ZstdEncoders.Rent(0);
        Assert.Equal(1, fast.Level);
        Assert.Equal(ZstdCompressor.DefaultLevel, standard.Level);
        ZstdEncoders.Return(fast);
        ZstdEncoders.Return(standard);

        // Whichever compressors the other tests give back meanwhile, a rental is of its level.
        ZstdCompressor again = ZstdEncoders.Rent(1);
        Assert.Equal(1, again.Level);
        ZstdEncoders.Return(again);
        ZstdCompressor strong = ZstdEncoders.Rent(19);
        Assert.Equal(19, strong.Level);
        ZstdEncoders.Return(strong);
    }

    /// <summary>Log lines of a few hundred distinct words: text zstd shrinks, and shrinks more at a higher level.</summary>
    private static Line[] Lines()
    {
        Random random = new Random(42);
        Line[] rows = new Line[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int words = 6 + random.Next(6);
            rows[row] = new Line(string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[random.Next(Words.Length)] + random.Next(40))));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Line[] rows, int level, int degree)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-zstd-level-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            ZstdLevel = level,
            DegreeOfParallelism = degree,
            Identity = Pinned,
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(nameof(Line.Text), EncodingHint.Zstd),
        };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(path, options);
        await writer.WriteAsync<Line>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    [VortexRecord]
    public partial record struct Line(string Text);
}
