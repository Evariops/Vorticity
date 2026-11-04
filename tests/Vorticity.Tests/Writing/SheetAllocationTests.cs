// A column's sheet takes its working arrays from the process's shelf and gives them back as they grow and when it is done:
// once the shelf holds them, a sheet of an object's column allocates nothing but itself.
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
public sealed class SheetAllocationTests
{
    /// <summary>
    /// A sheet of 65 536 rows of 20 000 distinct integers, fed in parts of 8 192 as a writer feeds it, its rows numbered and
    /// hashed for the column's counts, then let go: past the first, a sheet allocates its own object alone, as one fed
    /// nothing does, where its table, grown four times, its numbers, its hashes and its values came new each time, 3.76 MB.
    /// </summary>
    [Fact]
    public void ASheetLetGoAllocatesNothingButItselfOnceTheShelfHoldsItsArrays()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.Nullable);
        const int Part = 8_192;
        const int Rows = 65_536;
        CanonicalArena[] arenas = new CanonicalArena[Rows / Part];
        int[] nodes = new int[arenas.Length];
        for (int part = 0; part < arenas.Length; part++)
        {
            arenas[part] = new CanonicalArena();
            VortexBuffer buffer = arenas[part].Allocate(Part * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Part; row++)
            {
                values[row] = ((part * Part) + row) % 20_000;
            }

            nodes[part] = arenas[part].AddPrimitive(i64, Part, Validity.NonNullable, PType.I64, buffer);
        }

        for (int warm = 0; warm < 3; warm++)
        {
            Assert.Equal(20_000, Feed());
        }

        long bare = GC.GetAllocatedBytesForCurrentThread();
        new ColumnSheetBuilder().Dispose();
        bare = GC.GetAllocatedBytesForCurrentThread() - bare;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int distinct = Feed();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(20_000, distinct);
        Assert.True(allocated == bare, $"{allocated} bytes allocated by a sheet of {Rows} rows let go, {bare} by one fed nothing");

        int Feed()
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            sheet.NumberRows();
            sheet.HashRows();
            for (int part = 0; part < arenas.Length; part++)
            {
                sheet.Accumulate(arenas[part], nodes[part], 0, Part);
            }

            int counted = sheet.Distinct;
            sheet.Dispose();
            return counted;
        }
    }
}
