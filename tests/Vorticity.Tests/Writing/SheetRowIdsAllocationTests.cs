// A column's sheet numbers its rows in an array it takes from the process's shelf, which lends by length: taken at the
// power of two above a batch's rows, the sheets of the next objects find it there whatever their batches' counts.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class SheetRowIdsAllocationTests
{
    /// <summary>
    /// A hundred sheets, each numbering the rows of one batch of 4 098 to 4 197 rows of a key of 1 000 values, then let go:
    /// past the first, they allocate what sheets fed nothing do, where each batch's count asked of the shelf missed it and
    /// made an array of its own, as the variant paths of a consolidation of log records did with every chunk.
    /// </summary>
    [Fact]
    public void SheetsOfBatchesOfEveryCountFindTheirRowNumbersOnTheShelf()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        const int Rows = 4_200;
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = row % 1_000;
        }

        int node = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        Fed(4_097);
        long bare = GC.GetAllocatedBytesForCurrentThread();
        for (int count = 4_098; count < 4_198; count++)
        {
            Fed(0);
        }

        bare = GC.GetAllocatedBytesForCurrentThread() - bare;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int count = 4_098; count < 4_198; count++)
        {
            Fed(count);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == bare, $"{allocated} bytes allocated by a hundred sheets of a batch each, {bare} by sheets fed nothing");

        void Fed(int count)
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            sheet.NumberRows();
            if (count > 0)
            {
                sheet.Accumulate(arena, node, 0, count);
            }

            sheet.Dispose();
        }
    }
}
