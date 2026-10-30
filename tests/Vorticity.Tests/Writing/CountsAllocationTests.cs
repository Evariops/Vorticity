// A column's counts take their working arrays from the process's shelf, their measures' sketches held in tables of its
// arrays too, and give them back as they grow and when they are let go: once the shelf holds them,
// the counts of an object's column allocate nothing but themselves.
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
public sealed class CountsAllocationTests
{
    /// <summary>
    /// The counts of a key of 4 000 values over 57 344 rows, fed in parts of 8 192 as a writer feeds them, with an amount and
    /// a price to sketch for each value, then let go: past the first, they and their sheet allocate their own objects
    /// alone, as counts fed nothing do, where their 8 000 sketches came new with their arrays and their entries' records
    /// grew by doubling (7.3 MB at 65 536 rows), then their records alone (1 MB).
    /// </summary>
    [Fact]
    public void CountsLetGoAllocateNothingButThemselvesOnceTheShelfHoldsTheirArrays()
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("Key", VortexType.Int64),
            new VortexField("Amount", VortexType.Int64),
            new VortexField("Price", VortexType.Float64)]);
        DTypeArena types = new DTypeArena();
        CountsPlan plan = CountsPlan.For(VortexTypes.ToDType(schema, types))!;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        const int Part = 8_192;
        const int Rows = 7 * Part;
        const int Values = 4_000;
        CanonicalArena[] arenas = new CanonicalArena[Rows / Part];
        int[][] nodes = new int[arenas.Length][];
        for (int part = 0; part < arenas.Length; part++)
        {
            CanonicalArena arena = arenas[part] = new CanonicalArena();
            nodes[part] =
            [
                Column(arena, i64, PType.I64, row => row % Values),
                Column(arena, i64, PType.I64, row => row),
                Column(arena, f64, PType.F64, row => BitConverter.DoubleToInt64Bits(row * 0.5)),
            ];

            int Column(CanonicalArena arena, DType dtype, PType ptype, Func<long, long> bits)
            {
                VortexBuffer buffer = arena.Allocate(Part * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int row = 0; row < Part; row++)
                {
                    values[row] = bits(((long)part * Part) + row);
                }

                return arena.AddPrimitive(dtype, Part, Validity.NonNullable, ptype, buffer);
            }
        }

        for (int warm = 0; warm < 3; warm++)
        {
            Assert.Equal(Values, Feed(arenas.Length));
        }

        long bare = GC.GetAllocatedBytesForCurrentThread();
        Feed(parts: 0);
        bare = GC.GetAllocatedBytesForCurrentThread() - bare;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int distinct = Feed(arenas.Length);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Values, distinct);
        Assert.True(allocated == bare, $"{allocated} bytes allocated by counts of {Rows} rows let go, {bare} by counts fed nothing");

        int Feed(int parts)
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            ColumnCountsBuilder counts = new ColumnCountsBuilder(sheet, plan, field: 0);
            for (int part = 0; part < parts; part++)
            {
                plan.Prepare(arenas[part], nodes[part], Part);
                sheet.Accumulate(arenas[part], nodes[part][0], 0, Part);
                counts.Accumulate(arenas[part], nodes[part][0], 0, Part);
            }

            int counted = sheet.Distinct;
            counts.Dispose();
            sheet.Dispose();
            return counted;
        }
    }
}
