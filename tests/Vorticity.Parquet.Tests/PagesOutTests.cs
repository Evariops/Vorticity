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
            Assert.Equal(5, report.RowGroupCount);
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

    /// <summary>
    /// Every page whose values are stored as they are starts them on a 64-byte boundary of the file,
    /// dictionary pages and v1 pages among them, whatever the chunk's place, across row groups and
    /// pages cut by bytes; and the file reads as its unaligned twin does.
    /// </summary>
    [Theory]
    [InlineData(DataPageVersion.V2)]
    [InlineData(DataPageVersion.V1)]
    public async Task UncompressedValuesStartOnABoundaryOfTheFile(DataPageVersion pages)
    {
        ParquetWriteOptions options = new() { Profile = CompressionProfile.None, BlockRows = 1_000, RowGroupRows = 4_000, PageBytes = 3_000, DataPageVersion = pages };
        string aligned = await WriteMixedAsync(options);
        string unaligned = await WriteMixedAsync(options with { AlignUncompressedPages = false });

        WrittenFile file = new(await System.IO.File.ReadAllBytesAsync(aligned, Ct));
        int checkedPages = 0;
        for (int group = 0; group < file.Footer.RowGroups.Length; group++)
        {
            for (int column = 0; column < file.Schema.Columns.Length; column++)
            {
                Metadata.ColumnChunkMetadata chunk = file.Footer.Chunk(group, column);
                if (chunk.DictionaryPageOffset >= 0)
                {
                    Metadata.PageHeader dictionary = Metadata.PageHeader.Read(file.Bytes.AsSpan(checked((int)chunk.DictionaryPageOffset)));
                    Assert.Equal(0, (chunk.DictionaryPageOffset + dictionary.HeaderLength) % 64);
                    checkedPages++;
                }

                foreach (Metadata.PageLocation page in file.Pages(group, column))
                {
                    Metadata.PageHeader header = file.Header(page);
                    int lead = pages == DataPageVersion.V2
                        ? header.RepetitionLevelsLength + header.DefinitionLevelsLength
                        : LevelSections(file.Bytes.AsSpan(checked((int)page.Offset) + header.HeaderLength), file.Schema.Columns[column]);
                    if (header.UncompressedPageSize > lead)
                    {
                        Assert.Equal(0, (page.Offset + header.HeaderLength + lead) % 64);
                        checkedPages++;
                    }
                }
            }
        }

        Assert.True(checkedPages > 60, $"{checkedPages} pages");
        Assert.True(new System.IO.FileInfo(aligned).Length > new System.IO.FileInfo(unaligned).Length);
        Assert.Equal(await RowsAsync(unaligned), await RowsAsync(aligned));
        await using ParquetFile verified = await ParquetFile.OpenAsync(aligned, Ct);
        Assert.Empty(await verified.VerifyAsync(Ct));

        // A v1 page's levels lie inside its bytes, each kind behind its length.
        static int LevelSections(ReadOnlySpan<byte> page, Schema.ParquetColumn column)
        {
            int at = 0;
            foreach (int max in (int[])[column.MaxRepetitionLevel, column.MaxDefinitionLevel])
            {
                if (max > 0)
                {
                    at += sizeof(int) + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(page[at..]);
                }
            }

            return at;
        }
    }

    /// <summary>
    /// On a mapped file, two columns' values lie as far apart as the file puts them: neither was copied
    /// to be aligned, as values read in place from an unaligned page are at their first read.
    /// </summary>
    [Fact]
    public async Task AMappedPageIsItsColumn()
    {
        VortexSchema schema = [("a", VortexType.Int64), ("b", VortexType.Int64)];
        string path = NewPath();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, new ParquetWriteOptions { Profile = CompressionProfile.None }))
        {
            ColumnsBuilder builder = writer.Builder();
            // A page a column, which the first batch reads whole.
            for (int i = 0; i < 8_192; i++)
            {
                builder.Column<long>(0).Append(i * 3L);
                builder.Column<long>(1).Append(-i);
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        WrittenFile written = new(await System.IO.File.ReadAllBytesAsync(path, Ct));
        long ValuesAt(int column)
        {
            Metadata.PageLocation page = written.Pages(0, column)[0];
            Metadata.PageHeader header = written.Header(page);
            return page.Offset + header.HeaderLength + header.RepetitionLevelsLength + header.DefinitionLevelsLength;
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        // Borrowed batches, which hold what the scan read; an owned one is a copy by its nature.
        await foreach (BatchView batch in file.Scan().WithCancellation(Ct))
        {
            ReadOnlySpan<long> a = batch.Column<long>(0).Values;
            ReadOnlySpan<long> b = batch.Column<long>(1).Values;
            Assert.Equal(0L, ValuesAt(0) % 64);
            Assert.Equal(ValuesAt(1) - ValuesAt(0), (long)System.Runtime.CompilerServices.Unsafe.ByteOffset(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(a),
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(b)));
            Assert.Equal(3L, a[1]);
            break;
        }
    }

    private async Task<string> WriteMixedAsync(ParquetWriteOptions options)
    {
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("small", VortexType.Int32.Nullable),
            ("label", VortexType.Utf8),
            ("flag", VortexType.Bool.Nullable),
            ("tags", VortexType.List(VortexType.Int32)),
        ];
        string path = NewPath();
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        for (int i = 0; i < 10_000; i++)
        {
            builder.Column<long>(0).Append(i);
            if (i % 7 == 0)
            {
                builder.Column<int?>(1).AppendNull();
                builder.Column<bool?>(3).AppendNull();
            }
            else
            {
                builder.Column<int?>(1).Append(i % 500);
                builder.Column<bool?>(3).Append(i % 3 == 0);
            }

            builder.Column<string>(2).Append($"label-{i % 40}");
            ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(4);
            tags.BeginList();
            for (int t = 0; t < i % 4; t++)
            {
                tags.Elements.Append(i + t);
            }

            tags.EndList();
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static async Task<string[][]> RowsAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        List<string[]> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add([.. System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, batch.Schema.Count), c => Render.Row(batch, c, r))]);
                }
            }
        }

        return [.. rows];
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
