// A column's locate builder grows its arrays on the process's shelf, at powers of two, gives them back when it is done, and
// reads its sheet's values where they lie: the builders of the next objects find the arrays again and copy no value.
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
public sealed class LocateBuilderAllocationTests
{
    /// <summary>
    /// A hundred locate builders, each over eight granules of 1 024 rows of a key of 1 024 values, a value to a granule,
    /// finished then let go: past the first, they allocate the indexes they return and a few hundred bytes each, where
    /// each grew arrays of its own as values and pairs came and copied every value to sort them, as the builders of each
    /// column and shredded key of each object of a consolidation of log records did.
    /// </summary>
    [Fact]
    public void LocateBuildersFindTheirArraysOnTheShelfAndCopyNoValue()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        const int Granule = 1_024;
        const int Rows = 8 * Granule;
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = row / 8;
        }

        int node = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        Built();
        long returned = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int builder = 0; builder < 100; builder++)
        {
            returned += Built();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated - returned < 100 * 1_024, $"{allocated} bytes allocated by a hundred builders, {returned} of them the indexes they returned");

        long Built()
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            LocateBuilder locate = new LocateBuilder(sheet);
            for (int granule = 0; granule < 8; granule++)
            {
                sheet.Accumulate(arena, node, granule * Granule, Granule);
                locate.Accumulate(arena, node, granule * Granule, Granule);
                sheet.CloseBlock();
                locate.CloseBlock();
            }

            byte[] index = Assert.IsType<byte[]>(locate.Finish(Rows, Granule));
            locate.Dispose();
            sheet.Dispose();
            return index.Length;
        }
    }
}
