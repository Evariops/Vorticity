// A writer that knows a bound on its rows keeps its chunks long enough that the footer, a flat layout
// per chunk and column, stays within half the tables a traversal may visit, whatever their bytes.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class ChunkBoundTests
{
    private const int Columns = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheLeastChunkKeepsTheFooterWithinHalfItsCap()
    {
        DType schema = VortexTypes.ToDType(Wide.Schema, new DTypeArena());
        Assert.Equal(0, VortexFileWriter.LeastChunkBlocks(0, schema, 8_192));

        // A day of a period's object: 10^9 rows of 20 columns, 4 × 10^4 rows a chunk at least, in blocks.
        long blocks = VortexFileWriter.LeastChunkBlocks(1_000_000_000, schema, 8_192);
        Assert.Equal(5, blocks);
        long chunks = 1_000_000_000 / (blocks * 8_192) + 1;
        Assert.True(chunks * Columns <= VortexLimits.MaxFlatBufferTables / 2, $"{chunks} chunks of {Columns} columns");
    }

    /// <summary>
    /// A bound of 10^10 rows over 20 columns asks for 4 × 10^5 rows a chunk, past the megabyte of the
    /// widest column a writer aims at otherwise: the bound sets the length.
    /// </summary>
    [Fact]
    public async Task ABoundLengthensTheChunksItsFooterNeeds()
    {
        Wide[] rows = [.. Enumerable.Range(0, 1_000_000).Select(i => Row(i))];
        int[] free = await ChunksAsync(rows, rowBound: 0);
        int[] bounded = await ChunksAsync(rows, rowBound: 10_000_000_000);
        long least = VortexFileWriter.LeastChunkBlocks(10_000_000_000, VortexTypes.ToDType(Wide.Schema, new DTypeArena()), 8_192) * 8_192;
        Assert.True(free[0] < least, $"{free[0]} rows a chunk without a bound");

        // Only the file's end is shorter: the whole blocks left, then the rows of a block not filled.
        Assert.All(bounded[..^2], chunk => Assert.True(chunk >= least, $"{chunk} rows against {least}"));
        Assert.True(bounded[^2] + bounded[^1] < least);
        Assert.True(bounded.Length < free.Length);
    }

    private static async Task<int[]> ChunksAsync(Wide[] rows, long rowBound)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-chunk-bound-{Guid.NewGuid():N}.vortex");
        try
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path, new VortexWriteOptions { RowBound = rowBound });
            await writer.WriteAsync<Wide>(rows, Ct);
            WriteReport report = await writer.CompleteAsync(Ct);
            return [.. report.ChunkRowsOf(0)];
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Wide Row(long i) => new(i, i * 3, i * 5, i * 7, i * 11, i * 13, i * 17, i * 19, i * 23, i * 29,
        i * 31, i * 37, i * 41, i * 43, i * 47, i * 53, i * 59, i * 61, i * 67, i * 71);

    [VortexRecord]
    public partial record struct Wide(
        long C00, long C01, long C02, long C03, long C04, long C05, long C06, long C07, long C08, long C09,
        long C10, long C11, long C12, long C13, long C14, long C15, long C16, long C17, long C18, long C19);
}
