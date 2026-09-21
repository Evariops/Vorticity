// The bounded string extremes — `vortex.bounded_min(n)` and `vortex.bounded_max(n)` in a string
// column's zones, written when `StringBoundBytes` asks.
//
// TWO ORACLES. The cut itself is held to the reference's own test cases (vortex-array-0.86.1
// scalar/truncation.rs and aggregate_fn/fns/bounded_max), plus the edges its rules imply and its
// tests do not spell: a last character whose successor is wider, a surrogate, the last scalar
// value. The writer is held to the rule "each block's exact extremes, cut once", computed here from
// the rows themselves, on columns whose blocks include an all-null one, values longer than the
// limit with a character straddling the cut, and maxima no cut can bound.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class StringBoundsTests
{
    private const int Rows = 5_000;
    private const int Block = 1_024;
    private const int Limit = 8;

    // ------------------------------------------------------------------------------ the cut

    [Fact]
    public void TheCutsAreTheReferencesOnItsOwnCases()
    {
        Assert.Equal([0, 5], StringBounds.LowerBound([0, 5, 47, 33, 129], 2, utf8: false));
        Assert.Equal([0, 6, 0], StringBounds.UpperBound([0, 5, 255, 234, 23], 3, utf8: false));
        Assert.Null(StringBounds.UpperBound([255, 255, 255], 2, utf8: false));

        Assert.Equal("snowman", Utf8(StringBounds.LowerBound(Bytes("snowman⛄️snowman"), 9, utf8: true)));
        Assert.Equal("chas", Utf8(StringBounds.UpperBound(Bytes("char🪩"), 5, utf8: true)!));
        Assert.Null(StringBounds.UpperBound(Bytes("🂑🂒🂓"), 2, utf8: true));

        // `bounded_max_truncates_utf8_to_upper_bound`: the maximum of ["aardvark", "char🪩"] at 5.
        Assert.Equal("chas", Utf8(StringBounds.UpperBound(Bytes("char🪩"), 5, utf8: true)!));
    }

    [Fact]
    public void AValueWithinTheLimitIsItsOwnBoundBothWays()
    {
        Assert.Equal("abc", Utf8(StringBounds.LowerBound(Bytes("abc"), 3, utf8: true)));
        Assert.Equal("abc", Utf8(StringBounds.UpperBound(Bytes("abc"), 3, utf8: true)!));
        Assert.Equal([255, 255], StringBounds.UpperBound([255, 255], 2, utf8: false));
        Assert.Empty(StringBounds.LowerBound([], 1, utf8: false));
        Assert.Empty(StringBounds.UpperBound([], 1, utf8: true)!);
    }

    [Fact]
    public void TheUtf8IncrementTouchesTheLastCharacterOnly()
    {
        // U+007F's successor takes two bytes: no bound, and the character before is not tried.
        Assert.Null(StringBounds.UpperBound(Bytes("abcd"), 3, utf8: true));

        // U+D7FF's successor is a surrogate, not a character.
        Assert.Null(StringBounds.UpperBound(Bytes("a퟿b"), 4, utf8: true));

        // U+10FFFF has no successor.
        Assert.Null(StringBounds.UpperBound(Bytes("a\U0010FFFFb"), 5, utf8: true));

        // A two-byte character increments in place: é (C3 A9) becomes ê (C3 AA).
        Assert.Equal("aê", Utf8(StringBounds.UpperBound(Bytes("aébc"), 3, utf8: true)!));

        // The cut falls on the boundary at or below the limit: "k01060⛄" at 8 is "k01060", then "k01061".
        Assert.Equal("k01060", Utf8(StringBounds.LowerBound(Bytes("k01060⛄⛄"), 8, utf8: true)));
        Assert.Equal("k01061", Utf8(StringBounds.UpperBound(Bytes("k01060⛄⛄"), 8, utf8: true)!));
    }

    [Fact]
    public void KeepingTheFirstLimitPlusOneBytesCutsLikeTheWholeValue()
    {
        string[] values = ["k01060⛄⛄⛄", "abcdefgh", "abcdefghi", "abcdefgézz", ""];
        foreach (string text in values)
        {
            byte[] whole = Bytes(text);
            byte[] kept = whole.AsSpan(0, Math.Min(whole.Length, Limit + 1)).ToArray();
            Assert.Equal(StringBounds.LowerBound(whole, Limit, utf8: true), StringBounds.LowerBound(kept, Limit, utf8: true));
            Assert.Equal(StringBounds.UpperBound(whole, Limit, utf8: true), StringBounds.UpperBound(kept, Limit, utf8: true));
        }
    }

    // ------------------------------------------------------------------------------ the writer

    [Fact]
    public async Task EveryZoneCarriesItsBlocksExtremesCut()
    {
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, Rows, Limit);
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await AssertZonesAsync(file, "u", utf8: true);
            await AssertZonesAsync(file, "b", utf8: false);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task WithoutTheOptionAStringZoneHasNoBounds()
    {
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, Rows, 0);
            await using VortexFile file = await VortexFile.OpenAsync(path);
            ZoneColumn? zones = await ZonesOf(file, "u");
            Assert.NotNull(zones);
            for (int z = 0; z < Blocks; z++)
            {
                Assert.False(zones.Bounds(z).HasMin);
                Assert.False(zones.Bounds(z).HasMax);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    public static TheoryData<string> Filters() => new() { "eq", "lt", "gt", "prefix", "eq-binary" };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task TheBoundsPruneAndNeverDropARow(string name)
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = name switch
        {
            "eq" => Expr.Eq(Expr.Field("u"), Expr.Literal(FilterLiteral.From("k03500"))),
            "lt" => Expr.Lt(Expr.Field("u"), Expr.Literal(FilterLiteral.From("k00900"))),
            "gt" => Expr.Gt(Expr.Field("u"), Expr.Literal(FilterLiteral.From("k04500"))),
            "prefix" => Expr.StartsWith(Expr.Field("u"), FilterLiteral.From("k035")),
            _ => Expr.Eq(Expr.Field("b"), Expr.Literal(FilterLiteral.From(B(3_500)))),
        };

        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, Rows, Limit);
            await using VortexFile file = await VortexFile.OpenAsync(path);
            List<string> pruned = await FilteredRows(file, filter, pruning: true);
            List<string> all = await FilteredRows(file, filter, pruning: false);
            Assert.NotEmpty(all);
            Assert.Equal(all, pruned);

            BlockMask? live = (await ZonePruningPlan.PlanAsync(file, file.LayoutTree, filter, CancellationToken.None)).Live;
            Assert.NotNull(live);
            Assert.True(live.LiveCount < live.BlockCount, $"{name}: {live.LiveCount} of {live.BlockCount} blocks live");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnAppendKeepsTheOldZonesBounds()
    {
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 2_500, Limit);
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(
                path, new VortexWriteOptions { StringBoundBytes = Limit }))
            {
                await FeedAsync(writer, 2_500, Rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            Assert.Equal(Rows, file.RowCount);
            await AssertZonesAsync(file, "u", utf8: true);
            await AssertZonesAsync(file, "b", utf8: false);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnAppendOverZonesWithoutBoundsWritesNone()
    {
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 2_500, 0);
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(
                path, new VortexWriteOptions { StringBoundBytes = Limit }))
            {
                await FeedAsync(writer, 2_500, Rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            ZoneColumn? zones = await ZonesOf(file, "u");
            Assert.NotNull(zones);
            for (int z = 0; z < Blocks; z++)
            {
                Assert.False(zones.Bounds(z).HasMin, $"zone {z} has a lower bound the old zones never had");
            }

            Assert.Equal(
                await FilteredRows(file, Expr.Eq(Expr.Field("u"), Expr.Literal(FilterLiteral.From("k04000"))), pruning: false),
                await FilteredRows(file, Expr.Eq(Expr.Field("u"), Expr.Literal(FilterLiteral.From("k04000"))), pruning: true));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ the oracle

    private static int Blocks => (Rows + Block - 1) / Block;

    private static async Task AssertZonesAsync(VortexFile file, string name, bool utf8)
    {
        ZoneColumn? zones = await ZonesOf(file, name);
        Assert.NotNull(zones);
        for (int z = 0; z < Blocks; z++)
        {
            int start = z * Block;
            int end = Math.Min(Rows, start + Block);
            byte[]? min = null;
            byte[]? max = null;
            int nulls = 0;
            for (int row = start; row < end; row++)
            {
                byte[]? value = utf8 ? U(row) : B(row);
                if (value is null)
                {
                    nulls++;
                    continue;
                }

                if (min is null || value.AsSpan().SequenceCompareTo(min) < 0)
                {
                    min = value;
                }

                if (max is null || value.AsSpan().SequenceCompareTo(max) > 0)
                {
                    max = value;
                }
            }

            ZoneBounds bounds = zones.Bounds(z);
            Assert.Equal(nulls, bounds.NullCount);
            Assert.False(bounds.IsExact);
            if (min is null)
            {
                Assert.False(bounds.HasMin, $"{name} zone {z}: a bound on a zone of nulls");
                Assert.False(bounds.HasMax, $"{name} zone {z}: a bound on a zone of nulls");
                continue;
            }

            Assert.True(bounds.HasMin, $"{name} zone {z}: no lower bound");
            Assert.Equal(StringBounds.LowerBound(min, Limit, utf8), bounds.Min.BytesValue.ToArray());
            byte[]? upper = StringBounds.UpperBound(max!, Limit, utf8);
            Assert.Equal(upper is not null, bounds.HasMax);
            if (upper is not null)
            {
                Assert.Equal(upper, bounds.Max.BytesValue.ToArray());
            }
        }
    }

    private static async Task<ZoneColumn?> ZonesOf(VortexFile file, string name) =>
        (await ZonePruningPlan.PlanAsync(file, file.LayoutTree, Expr.IsNotNull(Expr.Field(name)), CancellationToken.None))
            .Zones?.Column(name);

    private static async Task<List<string>> FilteredRows(VortexFile file, VortexExpr filter, bool pruning)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().Where(filter).WithPruning(pruning).WithIndexes(false).ExecuteAsync())
        {
            Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    // ------------------------------------------------------------------------------ the rows

    /// <summary>
    /// Ordered keys, so blocks hold disjoint ranges; every block but the all-null third has a long
    /// value with a character across the cut; the second block's maximum is nine U+007F, which no
    /// cut can bound.
    /// </summary>
    private static byte[]? U(int row)
    {
        if (row % 13 == 0 || (row >= 2 * Block && row < 3 * Block))
        {
            return null;
        }

        string key = "k" + row.ToString("D5", CultureInfo.InvariantCulture);
        return row switch
        {
            _ when row >= Block && row < 2 * Block && row % 100 == 50 => Bytes(new string('', 9)),
            _ when row % 100 == 60 => Bytes(key + "⛄⛄⛄"),
            _ => Bytes(key),
        };
    }

    /// <summary>
    /// Big-endian keys with a tail on some rows; the fourth block's maximum is nine 0xFF bytes,
    /// which no cut can bound, and the last block carries a value whose increment carries.
    /// </summary>
    private static byte[]? B(int row)
    {
        if (row % 11 == 0)
        {
            return null;
        }

        if (row >= 3 * Block && row < 4 * Block && row % 100 == 70)
        {
            return [255, 255, 255, 255, 255, 255, 255, 255, 255];
        }

        byte[] key = [0x10, (byte)(row >> 8), (byte)row];
        if (row % 7 != 0)
        {
            return key;
        }

        byte[] longer = new byte[12];
        key.CopyTo(longer, 0);
        for (int i = 3; i < longer.Length; i++)
        {
            longer[i] = row >= 4 * Block ? (byte)255 : (byte)(row + i);
        }

        return longer;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-strbounds-{Guid.NewGuid():N}.vortex");

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["id", "u", "b"],
        [
            Types.Primitive(PType.I64, Nullability.NonNullable),
            Types.Utf8(Nullability.Nullable),
            Types.Binary(Nullability.Nullable),
        ],
        Nullability.NonNullable);

    private static async Task WriteAsync(string path, int start, int end, int limit)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = 1L << 14,
            StringBoundBytes = limit,
        };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, options);
        await FeedAsync(writer, start, end);
        await writer.CompleteAsync();
    }

    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        int row = start;
        int size = 700;
        while (row < end)
        {
            int count = Math.Min(size, end - row);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                int root = arena.AddStruct(
                    Schema,
                    count,
                    Validity.NonNullable,
                    [Ids(arena, row, count), Views(arena, Schema.GetField(1), row, count, U), Views(arena, Schema.GetField(2), row, count, B)]);
                using RecordBatch batch = new RecordBatch(arena, root, row);
                await writer.WriteAsync(batch);
            }
            finally
            {
                arena.Reset();
            }

            row += count;
            size = size == 700 ? 1_531 : 700;
        }
    }

    private static int Ids(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = start + i;
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Views(CanonicalArena arena, DType dtype, int start, int count, Func<int, byte[]?> value)
    {
        int heapBytes = 0;
        for (int i = 0; i < count; i++)
        {
            heapBytes += value(start + i) is { Length: > 12 } v ? v.Length : 0;
        }

        VortexBuffer heap = arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> data);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 1, out Span<byte> raw);
        bytes.Clear();
        raw.Clear();
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            if (value(start + i) is not { } v)
            {
                continue;
            }

            raw[i >> 3] |= (byte)(1 << (i & 7));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, v.Length);
            if (v.Length <= 12)
            {
                v.CopyTo(view[4..]);
                continue;
            }

            v.AsSpan(0, 4).CopyTo(view[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], written);
            v.CopyTo(data[written..]);
            written += v.Length;
        }

        int mask = arena.AddBool(Types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
        return arena.AddVarBinView(dtype, count, Validity.Bitmap(mask), views, [heap]);
    }
}
