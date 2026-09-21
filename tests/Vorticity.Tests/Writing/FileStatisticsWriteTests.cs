// The file statistics segment the writer emits at CompleteAsync: per top-level
// field the exact min / max, the null count, and is_sorted / is_strict_sorted -- the two flags no
// reference-written file carries, because vortex-layout-0.86.1's file-level aggregation drops
// them, and the flags a key cursor's SortedColumn source exists on.
//
// THE ORDER IS THE REFERENCE'S: nulls below every value (a sorted nullable column has its nulls
// first), equal neighbours allowed by is_sorted and refused by is_strict_sorted, a NaN claiming
// nothing. And the two seams a streaming writer has -- a block boundary and a batch boundary
// inside a block -- are each given a column whose only descent sits exactly there.
using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class FileStatisticsWriteTests
{
    /// <summary>Three batches of 1 500 rows in blocks of 1 024: block seams at 1 024, 2 048, 3 072, 4 096; batch seams at 1 500 and 3 000.</summary>
    private const int Rows = 4_500;
    private const int Batch = 1_500;
    private const int Block = 1_024;

    private static readonly string[] Columns =
    [
        "strict_i64", "dups_u32", "nulls_first_i32", "nulls_late_i32", "floats_f64", "nans_f64",
        "keys_utf8", "shuffled_i64", "flag_bool", "desc_i64", "seam_i64", "batch_seam_i64",
    ];

    [Theory]
    [InlineData("strict_i64", true, true, 0UL, 1_000L, 1_000L + (3L * (Rows - 1)))]
    [InlineData("dups_u32", true, false, 0UL, 0L, (Rows - 1) / 7L)]
    [InlineData("nulls_first_i32", true, false, 500UL, 0L, (Rows - 1 - 500) / 3L)]
    [InlineData("nulls_late_i32", false, false, 1UL, 0L, Rows - 1L)]
    [InlineData("shuffled_i64", false, false, 0UL, 0L, -1L)]
    [InlineData("desc_i64", false, false, 0UL, 10_000L - (Rows - 1), 10_000L)]
    [InlineData("seam_i64", false, false, 0UL, 0L, -1L)]
    [InlineData("batch_seam_i64", false, false, 0UL, 0L, -1L)]
    public async Task IntegerColumnsCarryTheirOrderBoundsAndNulls(
        string column, bool sorted, bool strict, ulong nulls, long min, long max)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block });
        FieldStatistics stats = written.Field(column);

        Assert.True(stats.TryGetIsSorted(out bool isSorted), column + " should state is_sorted");
        Assert.Equal(sorted, isSorted);
        Assert.True(stats.TryGetIsStrictSorted(out bool isStrict), column + " should state is_strict_sorted");
        Assert.Equal(strict, isStrict);
        Assert.True(stats.TryGetStoredNullCount(out ulong nullCount));
        Assert.Equal(nulls, nullCount);

        Assert.True(stats.HasMin && stats.HasMax, column + " should carry exact bounds");
        Assert.Equal(StatPrecision.Exact, stats.MinPrecision);
        Assert.Equal(StatPrecision.Exact, stats.MaxPrecision);
        Assert.Equal(min, AsLong(stats.Min));
        if (max >= 0)
        {
            Assert.Equal(max, AsLong(stats.Max));
        }
    }

    [Fact]
    public async Task FloatsFollowIeeeWithSignedZerosEqualAndANaNClaimingNothing()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block });

        // -0.0 then +0.0 then non-decreasing with every key twice: sorted, and the two zeros are
        // one value, so not strict.
        FieldStatistics floats = written.Field("floats_f64");
        Assert.True(floats.TryGetIsSorted(out bool sorted) && sorted);
        Assert.True(floats.TryGetIsStrictSorted(out bool strict) && !strict);
        Assert.Equal(0.0, floats.Min.AsF64);
        Assert.Equal((Rows - 2) / 2 * 0.5, floats.Max.AsF64);

        // One NaN, and the column says nothing of its order; its bounds skip the NaN.
        FieldStatistics nans = written.Field("nans_f64");
        Assert.False(nans.TryGetIsSorted(out _));
        Assert.False(nans.TryGetIsStrictSorted(out _));
        Assert.Equal(0.0, nans.Min.AsF64);
        Assert.Equal(Rows - 1.0, nans.Max.AsF64);
    }

    [Fact]
    public async Task StringsAreOrderedBytewiseAndBoolsClaimNothing()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block });

        FieldStatistics keys = written.Field("keys_utf8");
        Assert.True(keys.TryGetIsSorted(out bool sorted) && sorted);
        Assert.True(keys.TryGetIsStrictSorted(out bool strict) && !strict);
        Assert.True(keys.TryGetStoredNullCount(out ulong nulls) && nulls == 0);
        Assert.False(keys.HasMin, "a string column has no bound in the file statistics yet (docs/11 §3.2: a policy)");

        FieldStatistics flags = written.Field("flag_bool");
        Assert.False(flags.TryGetIsSorted(out _));
        Assert.False(flags.HasMin);
        Assert.True(flags.TryGetStoredNullCount(out ulong boolNulls) && boolNulls == 0);
    }

    [Fact]
    public async Task TheSegmentCanBeSwitchedOff()
    {
        Decoders.EnsureRegistered();
        await using Written on = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block });
        await using Written off = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block, FileStatistics = false });

        Assert.True(on.File.HasFileStatistics);
        Assert.False(off.File.HasFileStatistics);
        Assert.Equal(Columns.Length, on.File.FileStatistics.FieldCount);
    }

    [Fact]
    public async Task TheReaderAnswersAWholeFileExtremeFromTheSegment()
    {
        // A whole-file min or max is answered from the file statistics, on a file this writer
        // produced: no segment read.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(new VortexWriteOptions { RowBlockSize = Block });

        ScanMetrics metrics = new ScanMetrics();
        FilterLiteral min = await written.File.ScanBuilder().WithMetrics(metrics).MinAsync("strict_i64");
        FilterLiteral max = await written.File.ScanBuilder().WithMetrics(metrics).MaxAsync("dups_u32");

        Assert.Equal(1_000L, min.SignedValue);
        Assert.Equal((ulong)((Rows - 1) / 7), max.UnsignedValue);
        Assert.Equal(0, metrics.SegmentRequests);

        // And the file-level prune sees the bounds.
        Assert.False(written.File.MayMatch(Expr.Gt(Expr.Field("desc_i64"), Expr.Literal(FilterLiteral.From(10_000L)))));
        Assert.True(written.File.MayMatch(Expr.Gt(Expr.Field("desc_i64"), Expr.Literal(FilterLiteral.From(9_999L)))));
    }

    private static long AsLong(ScalarValue value) =>
        value.Kind == ScalarValueKind.UInt64 ? (long)value.AsUInt64 : value.AsInt64;

    /// <summary>A file of the twelve columns, written in three batches, and opened.</summary>
    private sealed class Written : IAsyncDisposable
    {
        private readonly string _path;

        private Written(string path, VortexFile file)
        {
            _path = path;
            File = file;
        }

        internal VortexFile File { get; }

        internal FieldStatistics Field(string name)
        {
            Assert.True(File.HasFileStatistics, "the file should carry statistics");
            int index = File.DType.IndexOfField(name);
            Assert.True(index >= 0, "no column " + name);
            return File.FileStatistics.GetField(index);
        }

        internal static async Task<Written> CreateAsync(VortexWriteOptions options)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-filestats-{Guid.NewGuid():N}.vortex");
            await WriteAsync(path, options);
            return new Written(path, await VortexFile.OpenAsync(path, CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(_path);
        }

        private static async Task WriteAsync(string path, VortexWriteOptions options)
        {
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            DType i32n = types.Primitive(PType.I32, Nullability.Nullable);
            DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType boolean = types.Bool(Nullability.NonNullable);
            DType schema = types.Struct(
                Columns,
                [i64, u32, i32n, i32n, f64, f64, utf8, i64, boolean, i64, i64, i64],
                Nullability.NonNullable);

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Batch)
            {
                int count = Math.Min(Batch, Rows - start);
                int[] columns =
                [
                    Longs(arena, i64, start, count, i => 1_000L + (3L * i)),
                    UInts(arena, u32, start, count, i => (uint)(i / 7)),
                    NullableInts(arena, types, i32n, start, count, i => i < 500 ? null : (i - 500) / 3),
                    NullableInts(arena, types, i32n, start, count, i => i == 2_000 ? null : i),
                    Doubles(arena, f64, start, count, i => i == 0 ? -0.0 : ((i - 1) / 2) * 0.5),
                    Doubles(arena, f64, start, count, i => i == 100 ? double.NaN : i),
                    Strings(arena, utf8, start, count, i => "k" + (i / 3).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)),
                    Longs(arena, i64, start, count, i => (i * 7919L) % Rows),
                    Bools(arena, boolean, start, count, i => i % 2 == 0),
                    Longs(arena, i64, start, count, i => 10_000L - i),
                    Longs(arena, i64, start, count, i => i < Block ? i : 500L + (i - Block)),
                    Longs(arena, i64, start, count, i => i < Batch ? i : 100L + (i - Batch)),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using (RecordBatch batch = new RecordBatch(arena, root, start))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Longs(CanonicalArena arena, DType dtype, int start, int count, Func<int, long> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
        }

        private static int UInts(CanonicalArena arena, DType dtype, int start, int count, Func<int, uint> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(uint), sizeof(uint), out Span<byte> bytes);
            Span<uint> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.U32, buffer);
        }

        private static int Doubles(CanonicalArena arena, DType dtype, int start, int count, Func<int, double> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> bytes);
            Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.F64, buffer);
        }

        private static int NullableInts(
            CanonicalArena arena, DTypeArena types, DType dtype, int start, int count, Func<int, int?> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                int? v = value(start + i);
                values[i] = v ?? 0;
                if (v is not null)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            int mask = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
            return arena.AddPrimitive(dtype, count, Validity.Bitmap(mask), PType.I32, buffer);
        }

        private static int Bools(CanonicalArena arena, DType dtype, int start, int count, Func<int, bool> value)
        {
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                if (value(start + i))
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return arena.AddBool(dtype, count, Validity.NonNullable, bits, 0);
        }

        /// <summary>Short strings only: every view is inline, so there is no data buffer to fill.</summary>
        private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
        {
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
            bytes.Clear();
            for (int i = 0; i < count; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(value(start + i));
                Assert.True(utf8.Length <= 12, "the fixture's strings are inline views");
                Span<byte> view = bytes.Slice(i * 16, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
                utf8.CopyTo(view[4..]);
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
        }
    }
}
