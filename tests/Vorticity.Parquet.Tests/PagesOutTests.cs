using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Pages out without copies: over a path, a row group's compressed pages are written from the
/// buffers they were compressed into, in the flush's one gathered write, and the pipe's own memory
/// holds only what it is given to copy.
/// </summary>
public sealed class PagesOutTests : IDisposable
{
    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task APageIsNeverCopiedOnItsWayToAFile()
    {
        VortexSchema schema = [("id", VortexType.Int64), ("noise", VortexType.Int64), ("label", VortexType.Utf8)];
        string source = NewPath();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(source, schema))
        {
            ColumnsBuilder builder = writer.Builder();
            Random random = new(31);
            for (int i = 0; i < 300_000; i++)
            {
                builder.Column<long>(0).Append(i);
                builder.Column<long>(1).Append(random.NextInt64());
                builder.Column<string>(2).Append($"label-{random.Next(1_000_000):D7}");
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        // The pages are written by a session whose pool counts what it lends: the pipe's segments,
        // since the batches come from another session and the column writers stage on the engine's.
        CountingPool pool = new();
        await using VortexSession session = VortexSession.Create(options => options.MemoryPool = pool);
        string target = NewPath();
        await using (ParquetFile file = await ParquetFile.OpenAsync(source, Ct))
        {
            await using ParquetFileWriter writer = session.CreateParquetWriter(target, file.Schema, new ParquetWriteOptions { RowGroupRows = 65_536 });
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            ParquetWriteReport report = await writer.CompleteAsync(Ct);
            Assert.Equal(5, report.RowGroups);
        }

        // Five row groups went out from the buffers they were compressed into; the pipe held the
        // magic and the tail. Copied, they would have taken the file's length of its segments.
        long length = new System.IO.FileInfo(target).Length;
        Assert.True(length > 2L << 20, $"{length} bytes");
        Assert.True(pool.Rented <= 1L << 20, $"{pool.Rented} bytes rented for a file of {length}");

        await using ParquetFile written = await ParquetFile.OpenAsync(target, Ct);
        Assert.Equal(300_000, written.RowCount);
        Assert.Empty(await written.VerifyAsync(Ct));
    }

    /// <summary>A path of its own for each file, since a file the session has mapped stays mapped while it is cached.</summary>
    private string NewPath()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-out-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        return path;
    }

    /// <summary>The shared pool, counting the bytes it lends.</summary>
    private sealed class CountingPool : MemoryPool<byte>
    {
        private long _rented;

        internal long Rented => Interlocked.Read(ref _rented);

        public override int MaxBufferSize => Shared.MaxBufferSize;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            IMemoryOwner<byte> owner = Shared.Rent(minBufferSize);
            Interlocked.Add(ref _rented, owner.Memory.Length);
            return owner;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
