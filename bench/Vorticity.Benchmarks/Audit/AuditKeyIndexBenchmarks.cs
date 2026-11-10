// Audit IN-01 (a), IN-06: what a lookup through a sorted-runs index costs, the pruner built and a fresh mask refined,
// over a file of 65,536 blocks of 64 rows. Keys unique and scattered (each in one block), or in runs of 1,024 rows (each
// in sixteen blocks): a lookup decoded its segment of keys whole, and marked a key's rows one division at a time.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A sorted-runs index's refinement of a block mask, against the keys' layout and the literals asked.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditKeyIndexBenchmarks
{
    private const int BlockRows = 64;
    private const int Blocks = 65_536;

    /// <summary>How the keys lie: unique and scattered, or in runs of 1,024 rows.</summary>
    [Params("unique", "runs")]
    public string Keys { get; set; } = "unique";

    /// <summary>Keys the filter asks for.</summary>
    [Params(1, 16)]
    public int Literals { get; set; }

    private string _path = null!;
    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-keyindex-{Guid.NewGuid():N}.vortex");
        WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        long rows = (long)Blocks * BlockRows;
        FilterLiteral[] values = new FilterLiteral[Literals];
        for (int i = 0; i < Literals; i++)
        {
            values[i] = FilterLiteral.From(Key(i * rows / Literals));
        }

        _filter = Literals == 1 ? Expr.Eq(Expr.Field("key"), Expr.Literal(values[0])) : Expr.In(Expr.Field("key"), values);
        Console.WriteLine($"// {Keys}, {Literals} literals: {Prune()} blocks live");
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
        KeyIndexPruner pruner = await KeyIndexPruner.BuildAsync(_file, _filter, BlockRows, default).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The file offers no locating index.");
        BlockMask live = new BlockMask(_file.RowCount, BlockRows);
        await pruner.RefineAsync(_file, live, default).ConfigureAwait(false);
        return live.LiveCount;
    }

    private long Key(long row) => Keys == "unique" ? (row * 2_654_435_761L) & 0xFFFF_FFFFL : row / 1_024;

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
            WritePolicy = WritePolicy.None.For("key", IndexSpec.SortedRuns),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(_path, schema, options);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
