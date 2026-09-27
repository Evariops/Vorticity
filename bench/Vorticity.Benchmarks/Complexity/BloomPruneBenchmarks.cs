// What refining a scan's block mask by a column's Bloom filters costs, against the blocks and the
// literals of the filter.
//
// A file of `Blocks` blocks of 64 rows whose Int64 key column holds a key of its own on every row,
// scattered, under a Bloom index of three resolutions: a filter per block, per sixteen blocks, and
// above. The filter asks for `Literals` keys, an equality for one and an IN list for more, each held
// by one row and the rows spread over the file, so that about that many blocks stay live, the
// filters' false positives aside. The pruner is built and refines a fresh mask. Run in a checkout
// of the original and in the tree.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A Bloom index's pruning of a block mask, against the blocks and the filter's literals.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class BloomPruneBenchmarks
{
    /// <summary>Rows per block, the index's and the mask's.</summary>
    private const int BlockRows = 64;

    /// <summary>Blocks of the file.</summary>
    [Params(4_096, 65_536)]
    public int Blocks { get; set; }

    /// <summary>Keys the filter asks for.</summary>
    [Params(1, 64)]
    public int Literals { get; set; }

    private string _path = null!;
    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>Writes the file, opens it, and checks the pruning keeps the literals' blocks and kills most.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-bloomprune-{Guid.NewGuid():N}.vortex");
        WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();

        long rows = (long)Blocks * BlockRows;
        FilterLiteral[] values = new FilterLiteral[Literals];
        for (int i = 0; i < Literals; i++)
        {
            values[i] = FilterLiteral.From(Key(i * rows / Literals));
        }

        _filter = Literals == 1
            ? Expr.Eq(Expr.Field("key"), Expr.Literal(values[0]))
            : Expr.In(Expr.Field("key"), values);
        int live = Prune();
        if (live < Literals || live > Blocks / 4)
        {
            throw new InvalidOperationException($"The filters left {live} blocks for {Literals} literals.");
        }
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>The pruner built, and a fresh mask refined.</summary>
    [Benchmark]
    public int Prune() => PruneAsync().GetAwaiter().GetResult();

    private async Task<int> PruneAsync()
    {
        BloomPruner pruner = await BloomPruner.BuildAsync(_file, _filter, BlockRows, default).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The file offers no Bloom index.");
        BlockMask live = new BlockMask(_file.RowCount, BlockRows);
        await pruner.RefineAsync(_file, live, default).ConfigureAwait(false);
        return live.LiveCount;
    }

    /// <summary>Row <paramref name="row"/>'s key: a multiplication by an odd number, one to one on 32 bits.</summary>
    private static long Key(long row) => (row * 2_654_435_761L) & 0xFFFF_FFFFL;

    private async Task WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [i64], Nullability.NonNullable);
        int rows = Blocks * BlockRows;
        VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < rows; row++)
        {
            values[row] = Key(row);
        }

        int column = arena.AddPrimitive(i64, rows, Validity.NonNullable, PType.I64, buffer);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = BlockRows,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None.For("key", IndexSpec.Bloom(resolutions: 3)),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(_path, schema, options);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
