// A column's counts hold each measure's sketches in one table, whose arrays come from the process's shelf and go back
// when the counts are let go: once the shelf holds them, the counts of an object's column allocate no sketch.
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
    /// The counts of a key of 4 000 values over 65 536 rows, fed in parts of 8 192 as a writer feeds them, with an amount and
    /// a price to sketch for each value, then let go: past the first, they allocate less than a quarter of a kibibyte for
    /// each of their 8 000 sketches, where each sketch came new with its arrays: 7.3 MB, against 1 MB now, their entries'.
    /// </summary>
    [Fact]
    public void CountsLetGoAllocateNoSketchOnceTheShelfHoldsTheirTables()
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
        const int Rows = 65_536;
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
            Assert.Equal(Values, Feed());
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int distinct = Feed();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Values, distinct);
        const long Sketches = 2L * Values * 256;
        Assert.True(allocated < Sketches, $"{allocated} bytes allocated by counts of {Rows} rows let go, against {Sketches} for a quarter of a kibibyte a sketch");

        int Feed()
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            ColumnCountsBuilder counts = new ColumnCountsBuilder(sheet, plan, field: 0);
            for (int part = 0; part < arenas.Length; part++)
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
