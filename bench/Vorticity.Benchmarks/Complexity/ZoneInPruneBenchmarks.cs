// What pruning a float column's zones against an IN list costs, against the zones and the list.
//
// A file of `Zones` zones of 64 rows whose Float64 column increases, the zone maps decoded once and
// kept on the file. The filter is an IN of `Literals` floats that the column holds, spread over it,
// so that about that many zones stay live. A pruning plan over the zone maps alone is made. Run
// in a checkout of the original and in the tree.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Zone pruning of an IN list of floats, against the zones and the literals.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ZoneInPruneBenchmarks
{
    /// <summary>Rows per zone.</summary>
    private const int ZoneRows = 64;

    /// <summary>Zones of the file.</summary>
    [Params(1_024, 16_384)]
    public int Zones { get; set; }

    /// <summary>Floats the IN list names.</summary>
    [Params(64, 4_096)]
    public int Literals { get; set; }

    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>Writes and opens the file, and checks the plan leaves the literals' zones.</summary>
    [GlobalSetup]
    public void Setup()
    {
        byte[] bytes = WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), new VortexOpenOptions(), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        long rows = (long)Zones * ZoneRows;
        FilterLiteral[] values = new FilterLiteral[Literals];
        for (int i = 0; i < Literals; i++)
        {
            values[i] = FilterLiteral.From(Value(i * rows / Literals));
        }

        _filter = Expr.In(Expr.Field("f"), values);
        int live = Plan();
        if (live < Math.Min(Literals, Zones) || live > Math.Min(2 * Literals, Zones))
        {
            throw new InvalidOperationException($"The zone maps left {live} zones for {Literals} literals.");
        }
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>A pruning plan over the zone maps, which the file keeps after the first.</summary>
    [Benchmark]
    public int Plan() => PlanAsync().GetAwaiter().GetResult();

    private async Task<int> PlanAsync()
    {
        ZonePruningPlan.PruningPlan plan = await ZonePruningPlan.PlanAsync(
            _file, _file.LayoutTree, _filter, CancellationToken.None, indexes: false).ConfigureAwait(false);
        return plan.Live?.LiveCount ?? Zones;
    }

    /// <summary>Row <paramref name="row"/>'s value: increasing, with a fraction.</summary>
    private static double Value(long row) => (row * 0.5) + 0.25;

    private async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["f"], [f64], Nullability.NonNullable);
        int rows = Zones * ZoneRows;
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
        for (int row = 0; row < rows; row++)
        {
            values[row] = Value(row);
        }

        int column = arena.AddPrimitive(f64, rows, Validity.NonNullable, PType.F64, buffer);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);
        System.IO.MemoryStream written = new System.IO.MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = ZoneRows };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(written), schema, options))
        {
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return written.ToArray();
    }
}
