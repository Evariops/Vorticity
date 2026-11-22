// What the row encoder's write pass allocates once it is warm, which the process's block pool decides as much as the
// encoder does, so it is measured with nothing else renting from that pool.
using System;
using Vorticity.RowEncoding;
using Vorticity.Tests.Buffers;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

[Collection(nameof(SharedPoolCollection))]
[Trait("Category", "RunsAlone")]
public sealed class RowWriteAllocationTests
{
    /// <summary>
    /// The write pass allocates nothing once the pool is warm. Its buffers are rented, and the scratch a variable-width
    /// child needs is rented too: a per-call allocation there would be paid on every batch of a sort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The blocks come from the process's pool, one stack a size, and every test beside this one rents from it too. A
    /// neighbour whose demand passes what the pool keeps takes the blocks the pass gives back, and the pass's next rents
    /// allocate new ones, in every window: 392 bytes, a block of 64 and two of 128, on the scalar pass of CI on
    /// 2026-11-22, and 328 on the vector pass the same day. With a thread trimming the pool beside, 385 rounds of five
    /// windows out of 500 allocated, and none alone. So the test runs alone, as the pool's own tests do.
    /// </para>
    /// <para>
    /// The least of several windows is still what the pass cannot avoid: a background collection can count the rest of
    /// the thread's allocation context into the window it ends in, never into all of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWarmWritePassAllocatesNothing()
    {
        using RowFixture fixture = new RowFixture();
        int[] columns = RowTwoPassTests.EveryPath(fixture, out RowSortField[] fields);
        const int rows = 4;

        int[] sizes = new int[rows];
        int total = RowEncoder.ComputeSizes(fixture.Arena, columns, fields, sizes);
        int[] offsets = new int[rows];
        _ = RowEncoder.ComputeOffsets(sizes, offsets);
        byte[] destination = new byte[total];
        int[] cursors = new int[rows];

        // Warm the pool and the JIT.
        for (int i = 0; i < 8; i++)
        {
            Array.Clear(cursors);
            RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursors, destination);
        }

        long least = long.MaxValue;
        for (int window = 0; window < 5; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++)
            {
                Array.Clear(cursors);
                RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursors, destination);
            }

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, least);
    }
}
