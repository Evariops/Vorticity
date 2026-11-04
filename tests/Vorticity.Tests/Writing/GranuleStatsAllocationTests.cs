// A column's statistics over its granules take their working arrays from the process's shelf and give them back when they
// are let go, and read their sheet's values where they lie: the builders of the next objects find the arrays again and
// copy no value.
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
public sealed class GranuleStatsAllocationTests
{
    /// <summary>
    /// A hundred builders of a key's statistics over seven granules of 8 192 rows, 1 024 values a granule and an amount to
    /// measure, finished then let go: past the first, they allocate the statistics they return and a few kilobytes each,
    /// where each made its arrays of zeros, of values met and of their rows anew, and the buckets of its granules'
    /// histograms, 16 KB a builder.
    /// </summary>
    [Fact]
    public void GranuleStatisticsFindTheirArraysOnTheShelfAndCopyNoValue()
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("Key", VortexType.Int64),
            new VortexField("Amount", VortexType.Int64)]);
        DTypeArena types = new DTypeArena();
        DType dtype = VortexTypes.ToDType(schema, types);
        CountsPlan plan = CountsPlan.For(dtype)!;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        const int Part = 8_192;
        const int Parts = 7;
        CanonicalArena[] arenas = new CanonicalArena[Parts];
        int[][] nodes = new int[Parts][];
        for (int part = 0; part < Parts; part++)
        {
            CanonicalArena arena = arenas[part] = new CanonicalArena();
            nodes[part] = [Column(arena, row => (row / 8) % 7_168), Column(arena, row => row)];

            int Column(CanonicalArena arena, Func<long, long> value)
            {
                VortexBuffer buffer = arena.Allocate(Part * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int row = 0; row < Part; row++)
                {
                    values[row] = value(((long)part * Part) + row);
                }

                return arena.AddPrimitive(i64, Part, Validity.NonNullable, PType.I64, buffer);
            }
        }

        Built();
        long returned = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int builder = 0; builder < 100; builder++)
        {
            returned += Built();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated - returned < 100 * 8_192, $"{allocated} bytes allocated by a hundred builders, {returned} of them the statistics they returned");

        long Built()
        {
            ColumnSheetBuilder sheet = new ColumnSheetBuilder();
            GranuleStatsBuilder stats = new GranuleStatsBuilder(sheet, plan, 0, i64);
            for (int part = 0; part < Parts; part++)
            {
                plan.Prepare(arenas[part], nodes[part], Part);
                sheet.Accumulate(arenas[part], nodes[part][0], 0, Part);
                stats.Accumulate(arenas[part], nodes[part][0], 0, Part);
                sheet.CloseBlock();
                stats.CloseBlock();
            }

            (byte[] Statistics, byte[]? Histograms, long _, long _) = Assert.NotNull(stats.Finish(Parts * Part));
            stats.Dispose();
            sheet.Dispose();
            return Statistics.Length + (Histograms?.Length ?? 0);
        }
    }
}
