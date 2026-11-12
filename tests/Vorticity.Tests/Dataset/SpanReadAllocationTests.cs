using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// What a span read allocates once warm: its context and the arrays that name its chunks, whatever its rows, since decoded
/// values come from the pool. Measured alone, as every allocation ceiling is, over a source that lends views so that only
/// the reader's own bytes count.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class SpanReadAllocationTests
{
    [Fact]
    public async Task ASpanReadAllocatesItsContextAndNothingARow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SpanReaderTests.Written written = await SpanReaderTests.WriteAsync(contents: true, ct);
        await using MemorySegmentSource memory = new(written.Bytes);
        SpanReader reader = (await SpanReader.OpenAsync(memory, written.Bytes.Length, written.Schema, null, ct))!;

        // A first read warms the pool; the floor over the passes after it is the ceiling's figure.
        // 2 000 rows of two eight-byte columns decode 32 000 bytes, so a cost per row would pass it.
        const long First = 3 * SpanReaderTests.SpanRows;
        using (await reader.ReadRowsAsync(First, SpanReaderTests.SpanRows, null, ct))
        {
        }

        long floor = long.MaxValue;
        for (int pass = 0; pass < 5; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<RecordBatch> read = reader.ReadRowsAsync(First, SpanReaderTests.SpanRows, null, ct);
            Assert.True(read.IsCompletedSuccessfully);
            using (await read)
            {
                floor = Math.Min(floor, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        Assert.True(floor <= Ceiling, $"A span read of {SpanReaderTests.SpanRows} rows allocated {floor} bytes, above {Ceiling}.");
    }

    /// <summary>
    /// 1 296 bytes measured on x64 (2026-11-10) once the context comes from a pool and goes back with the batch; 18 664 when
    /// each read made its own, most of it the context's arenas (16 944 on arm64, 2026-10-19).
    /// </summary>
    private const long Ceiling = 4 * 1024;
}
