// What positioning a cursor over sorted runs at a rank costs, against the number of runs.
//
// A file of 196,608 rows written in `Runs` parts, a write then appends, each part whole blocks of
// 8,192 rows, whose key column spreads every part over the whole key range and carries a
// sorted-runs index: a write leaves one run, and an append keeps the runs before it up to three,
// so that the runs overlap everywhere. A batch of ranks spread over the entries is sought one
// after another. Run in a checkout of the original and in the tree.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A batch of rank seeks over overlapping sorted runs, against how many runs there are.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RankSeekBenchmarks
{
    /// <summary>The runs, one per part written: an append past the third folds them into one.</summary>
    [Params(1, 2, 3)]
    public int Runs { get; set; }

    /// <summary>The key column.</summary>
    [Params(Keyed.Int64, Keyed.Utf8)]
    public Keyed Column { get; set; }

    /// <summary>A key column.</summary>
    public enum Keyed
    {
        /// <summary>64-bit integers.</summary>
        Int64,

        /// <summary>Strings of sixteen bytes.</summary>
        Utf8,
    }

    private const int Rows = 3 << 16;

    private const int Probes = 64;

    private static readonly string[] Names = ["i64", "utf8"];

    private string _path = "";
    private VortexFile _file = null!;
    private KeyCursor _cursor = null!;

    /// <summary>Writes the file and opens the cursor.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-rank-{Guid.NewGuid():N}.vortex");
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(Names, [i64, utf8], Nullability.NonNullable);
        int partRows = Rows / Runs;
        VortexWriteOptions options = new VortexWriteOptions
        {
            DataBlockTargetBytes = null,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None
                .For("i64", IndexSpec.SortedRuns)
                .For("utf8", IndexSpec.SortedRuns),
        };

        for (int part = 0; part < Runs; part++)
        {
            await using VortexFileWriter writer = part == 0
                ? VortexFileWriter.Create(_path, schema, options)
                : await VortexFileWriter.AppendAsync(_path, options, CancellationToken.None);
            CanonicalArena arena = new CanonicalArena();
            int root = arena.AddStruct(
                schema, partRows, Validity.NonNullable, [Integers(arena, i64, part, partRows), Strings(arena, utf8, part, partRows)]);
            using (RecordBatch batch = new RecordBatch(arena, root, (long)part * partRows))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        _file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        string column = Column == Keyed.Int64 ? "i64" : "utf8";
        KeyPlan plan = await _file.Keys(column).WithSource(KeySourceKind.SortedRuns).ExplainAsync(CancellationToken.None);
        _cursor = await _file.Keys(column).WithSource(KeySourceKind.SortedRuns).OpenAsync(CancellationToken.None);
        long found = await Seek();
        if (plan.Runs != Runs || found != Probes)
        {
            throw new InvalidOperationException($"{plan.Runs} runs where {Runs} were written, {found} of {Probes} ranks found.");
        }
    }

    /// <summary>Closes the cursor and deletes the file.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cursor.DisposeAsync();
        await _file.DisposeAsync();
        System.IO.File.Delete(_path);
    }

    /// <summary>Every probe's rank sought in turn.</summary>
    [Benchmark]
    public async Task<long> Seek()
    {
        long found = 0;
        for (int probe = 0; probe < Probes; probe++)
        {
            long rank = ((2L * probe) + 1) * Rows / (2 * Probes);
            found += await _cursor.SeekRankAsync(rank, CancellationToken.None) ? 1 : 0;
        }

        return found;
    }

    /// <summary>The key of a part's row: every part spread over the whole range, the parts interleaved.</summary>
    private int KeyOf(int part, int row) => (row * Runs) + part;

    private int Integers(CanonicalArena arena, DType dtype, int part, int rows)
    {
        VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < rows; row++)
        {
            values[row] = KeyOf(part, row);
        }

        return arena.AddPrimitive(dtype, rows, Validity.NonNullable, PType.I64, buffer);
    }

    private int Strings(CanonicalArena arena, DType dtype, int part, int rows)
    {
        const int Length = 16;
        VortexBuffer heap = arena.Allocate(rows * Length, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> viewBytes);
        for (int row = 0; row < rows; row++)
        {
            Span<byte> value = heapBytes.Slice(row * Length, Length);
            Encoding.ASCII.GetBytes($"key-{KeyOf(part, row):D12}", value);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, Length);
            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], row * Length);
        }

        return arena.AddVarBinView(dtype, rows, Validity.NonNullable, views, [heap]);
    }
}
