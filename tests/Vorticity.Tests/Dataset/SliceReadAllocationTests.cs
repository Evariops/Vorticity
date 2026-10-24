using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// What a slice read allocates once warm: its context and the arrays that name its chunks, whatever
/// its rows, since decoded values come from the pool. Measured alone, as every allocation ceiling is,
/// over a source that lends views so that only the reader's own bytes count.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class SliceReadAllocationTests
{
    [Fact]
    public async Task ASliceReadAllocatesItsContextAndNothingARow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TimeSliceTests.Sliced sliced = await TimeSliceTests.WriteSlicedAsync(declare: true, ct);
        await using MemorySegmentSource memory = new(sliced.Bytes);
        SliceReader reader = (await SliceReader.OpenAsync(memory, sliced.Bytes.Length, sliced.Schema, null, ct))!;

        // A first read warms the pool; the floor over the passes after it is the ceiling's figure.
        // 2 000 rows of two eight-byte columns decode 32 000 bytes, so a cost per row would pass it.
        using (await reader.ReadSliceAsync(3, null, ct))
        {
        }

        long floor = long.MaxValue;
        for (int pass = 0; pass < 5; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<RecordBatch> read = reader.ReadSliceAsync(3, null, ct);
            Assert.True(read.IsCompletedSuccessfully);
            using (await read)
            {
                floor = Math.Min(floor, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        Assert.True(floor <= Ceiling, $"A slice read of {sliced.Table.Slices[3].Rows} rows allocated {floor} bytes, above {Ceiling}.");
    }

    /// <summary>16 944 bytes measured on arm64 (2026-10-19), most of it the context's arenas.</summary>
    private const long Ceiling = 20 * 1024;
}
