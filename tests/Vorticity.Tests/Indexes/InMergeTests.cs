// The read-side kernel for `IN (...)`: a sorted merge-join against a run's keys.
//
// WHAT THE MEASUREMENT FOUND, and why this test is about answers rather than time: the comparisons
// were never the cost. A thousand-key `IN` spent its planning merging the slices it had found so
// far on every literal — the whole list, a thousand times — and the pruner compared keys through
// their bytes where a fixed-width key is one unsigned integer in the run's own order. Both are
// gone; what must not change is the answer, on the shapes that exercise the two paths the pruner
// now chooses between: a few keys (a binary search each) and many (one walk).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class InMergeTests
{
    private const int Rows = 40_000;
    private const int Block = 1_024;

    /// <summary>The key of row <paramref name="row"/>: every value twice, in no order.</summary>
    private static long Key(int row) => ((row * 7919L) % (Rows / 2)) * 3;

    public static TheoryData<int, int> Shapes() => new()
    {
        { 1, 0 },
        { 8, 3 },
        { 200, 7 },
        { 2_000, 11 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AnInListAnswersTheRowsWhateverItsLength(int literals, int stride)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = await WriteAsync();
        try
        {
            List<long> wanted = [];
            for (int i = 0; i < literals; i++)
            {
                // Some of these keys are in the column and some are not; the duplicates are the
                // caller's own, which the merge must not double-count.
                wanted.Add(((i * (stride + 1) * 3L) % (Rows * 2)) - 5);
                if (i % 17 == 0)
                {
                    wanted.Add(wanted[^1]);
                }
            }

            FilterLiteral[] values = [.. wanted.Select(FilterLiteral.From)];
            VortexExpr filter = Expr.In(Expr.Field("key"), values);
            HashSet<long> set = [.. wanted];
            List<long> expected = [.. Enumerable.Range(0, Rows).Select(Key).Where(set.Contains)];

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(expected.Count, await file.ScanBuilder().Where(filter).CountAsync(ct));
            Assert.Equal(expected, await KeysAsync(file, filter));

            // The same answer with the indexes out of the way.
            Assert.Equal(expected, await KeysAsync(file, filter, prune: false));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheTwoZerosOfAFloatKeyAreOneAnswer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [f64], Nullability.NonNullable);
        const int rows = 4_096;
        VortexBuffer buffer = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int row = 0; row < rows; row++)
        {
            values[row] = row switch
            {
                17 => -0.0,
                2_000 => 0.0,
                _ => row + 0.5,
            };
        }

        int column = arena.AddPrimitive(f64, rows, Validity.NonNullable, PType.F64, buffer);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-inmerge-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options()))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            VortexExpr zero = Expr.In(Expr.Field("key"), FilterLiteral.From(0.0), FilterLiteral.From(1.5));
            // Row 1 holds 1.5; rows 17 and 2 000 hold the two zeros, which one literal asks for.
            List<double> selected = [];
            await foreach (RecordBatch batch in file.ScanBuilder().Where(zero).ExecuteAsync())
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    selected.Add(batch.Column(0).AsPrimitive<double>().Values[row]);
                }
            }

            Assert.Equal(
                [1.5, -0.0, 0.0],
                [.. selected.Select(BitConverter.DoubleToInt64Bits).Select(BitConverter.Int64BitsToDouble)]);
            Assert.Equal(
                [BitConverter.DoubleToInt64Bits(1.5), BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(0.0)],
                [.. selected.Select(BitConverter.DoubleToInt64Bits)]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static VortexWriteOptions Options() => new VortexWriteOptions
    {
        RowBlockSize = Block,
        IndexBudgetPerMille = 1_000_000,
        Identity = new Guid("32323232-3232-4232-8232-323232323232"),
        WritePolicy = WritePolicy.None.For("key", IndexSpec.SortedRuns),
    };

    private static async Task<string> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [i64], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = Key(row);
        }

        int column = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-inmerge-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options());
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch);
        }

        await writer.CompleteAsync();
        return path;
    }

    /// <summary>The key of every row the filter selects, in file order.</summary>
    private static async Task<List<long>> KeysAsync(VortexFile file, VortexExpr filter, bool prune = true)
    {
        List<long> keys = [];
        ScanBuilder scan = file.ScanBuilder().Where(filter);
        if (!prune)
        {
            scan = scan.WithPruning(false);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(batch.Column(0).AsPrimitive<long>().Values[row]);
            }
        }

        return keys;
    }
}
