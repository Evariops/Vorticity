// A chunk whose array tree declares no buffer, a progression written as a sequence: the writer
// inlines the tree in its flat's metadata, over the whole blob, and a scan reads the chunk from no
// segment at all. Readers before the editions of 2026 refuse a flat's metadata, so a write that
// targets one of them inlines nothing, and an append keeps the trees of the chunks it keeps.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Tests.Scan;
using Vorticity.Writing;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed partial class BufferlessFlatTests
{
    /// <summary>One block a chunk, four chunks.</summary>
    private const int Block = 8_192;

    private const int Rows = 4 * Block;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASequenceChunkCarriesItsTreeAndIsReadFromNoSegment()
    {
        string path = await WriteAsync(new VortexWriteOptions { ChunkTargetBytes = Block * sizeof(long) });
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<LayoutNode> ids = Chunks(file, 0);
            Assert.Equal(4, ids.Count);
            Assert.All(ids, flat => Assert.True(flat.Bufferless && !flat.Metadata.IsEmpty));
            Assert.All(Chunks(file, 1), flat => Assert.False(flat.Bufferless || !flat.Metadata.IsEmpty));

            Projection<long> read = file.Scan<Row>().Select(r => r.Id);
            Assert.Equal(Enumerable.Range(0, Rows).Select(row => (long)row), await read.ToListAsync(Ct));
            Assert.Equal(0, read.Metrics.Requests);

            Projection<double> values = file.Scan<Row>().Select(r => r.Value);
            Assert.Equal(Enumerable.Range(0, Rows).Select(Value), await values.ToListAsync(Ct));
            Assert.True(values.Metrics.Requests > 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AWriteForAReaderBeforeTheTreesInlinesNone()
    {
        string path = await WriteAsync(new VortexWriteOptions
        {
            ChunkTargetBytes = Block * sizeof(long),
            TargetEdition = VortexEdition.Core20251000,
        });
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.All(Chunks(file, 0), flat => Assert.False(flat.Bufferless || !flat.Metadata.IsEmpty));

            Projection<long> read = file.Scan<Row>().Select(r => r.Id);
            Assert.Equal(Enumerable.Range(0, Rows).Select(row => (long)row), await read.ToListAsync(Ct));
            Assert.True(read.Metrics.Requests > 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnAppendKeepsTheTreesOfTheChunksItKeeps()
    {
        VortexWriteOptions options = new VortexWriteOptions { ChunkTargetBytes = Block * sizeof(long) };
        string path = await WriteAsync(options, rows: Rows / 2);
        try
        {
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options, Ct))
            {
                await writer.WriteAsync<Row>(Make(Rows / 2, Rows), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<LayoutNode> ids = Chunks(file, 0);
            Assert.Equal(4, ids.Count);
            Assert.All(ids, flat => Assert.True(flat.Bufferless));

            Projection<long> read = file.Scan<Row>().Select(r => r.Id);
            Assert.Equal(Enumerable.Range(0, Rows).Select(row => (long)row), await read.ToListAsync(Ct));
            Assert.Equal(0, read.Metrics.Requests);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static double Value(int row) => (row * 7919 % 1_000_003) / 7.0;

    private static Row[] Make(int from, int to) =>
        [.. Enumerable.Range(from, to - from).Select(row => new Row(row, Value(row)))];

    private static async Task<string> WriteAsync(VortexWriteOptions options, int rows = Rows)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-bufferless-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, options);
        await writer.WriteAsync<Row>(Make(0, rows), Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    /// <summary>The flat chunks of column <paramref name="field"/>, under its zone map and its chunked layout.</summary>
    private static List<LayoutNode> Chunks(VortexFile file, int field)
    {
        LayoutNode column = LayoutTree.Parse(file).Root.GetChild(field);
        if (column.Encoding == LayoutEncodingId.Zoned)
        {
            column = column.GetChild(0);
        }

        Assert.Equal(LayoutEncodingId.Chunked, column.Encoding);
        List<LayoutNode> chunks = [];
        for (int i = 0; i < column.ChildCount; i++)
        {
            LayoutNode chunk = column.GetChild(i);
            Assert.Equal(LayoutEncodingId.Flat, chunk.Encoding);
            chunks.Add(chunk);
        }

        return chunks;
    }

    [VortexRecord]
    public partial record struct Row(long Id, double Value);
}
