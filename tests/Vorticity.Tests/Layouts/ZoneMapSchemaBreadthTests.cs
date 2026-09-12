// The zoned/stats schema derivations walk the COLUMN's dtype, which the file hands over as-is. Two
// things must hold for any dtype a DTypeArena can build, however wide:
//
//   * the walk must terminate - a deduplicating arena turns Struct(["a","b"], [d, d]) nested 64
//     deep into a 65-node DAG with 2^64 paths;
//   * it must not REFUSE ordinary breadth. A 5000-column feature block is four distinct arena nodes
//     and 5001 visits, and nothing in the format, in DTypeArena or in VortexLimits caps a struct's
//     field count, so rejecting it reports a legal file as malformed.
//
// A flat visit budget satisfies only the first. These pin the second.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using Vorticity.Layouts;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class ZoneMapSchemaBreadthTests
{
    private const int WideFieldCount = 5000;

    [Fact]
    public void ALegacyStatsTableOverAWideStructColumnIsDerivable()
    {
        DTypeArena arena = new DTypeArena();
        DType column = WideStruct(arena);

        // uncompressed_size_in_bytes is the stat whose support test walks the whole column dtype.
        ReadOnlySpan<LegacyStat> stats = [LegacyStat.UncompressedSizeInBytes, LegacyStat.NullCount];
        DType table = ZoneMapSchema.LegacyStatsTable(new DTypeArena(), column, stats);

        Assert.Equal(DTypeKind.Struct, table.Kind);
        Assert.Equal(2, table.FieldCount);
    }

    [Fact]
    public void ADeeplySharedChildColumnStillTerminates()
    {
        DTypeArena arena = new DTypeArena();
        DType node = arena.Primitive(PType.I32, Nullability.NonNullable);
        Span<int> names = stackalloc int[2];
        names[0] = arena.InternName("a"u8);
        names[1] = arena.InternName("b"u8);
        for (int i = 0; i < 62; i++)
        {
            Span<DType> children = [node, node];
            node = arena.Struct(names, children, Nullability.NonNullable);
        }

        ReadOnlySpan<LegacyStat> stats = [LegacyStat.UncompressedSizeInBytes];
        Stopwatch clock = Stopwatch.StartNew();
        DType table = ZoneMapSchema.LegacyStatsTable(new DTypeArena(), node, stats);
        clock.Stop();

        Assert.Equal(1, table.FieldCount);
        Assert.True(
            clock.ElapsedMilliseconds < 5_000,
            $"a 63-level shared-child DAG took {clock.ElapsedMilliseconds} ms to inspect");
    }

    private static DType WideStruct(DTypeArena arena)
    {
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int[] names = new int[WideFieldCount];
        DType[] fields = new DType[WideFieldCount];
        for (int i = 0; i < WideFieldCount; i++)
        {
            names[i] = arena.InternName(
                Encoding.UTF8.GetBytes("f" + i.ToString(CultureInfo.InvariantCulture)));
            fields[i] = i32;
        }

        return arena.Struct(names, fields, Nullability.NonNullable);
    }
}
