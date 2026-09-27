// What a distinct walk over a key source pays to move from one key to the next.
//
// A file of 262,144 rows written in four parts, a write and three appends, whose key columns hold
// each key on `Dups` rows. The sorted columns run in key order across the parts, the source a
// sorted column serves; the mixed columns spread every part over the whole key range and carry a
// sorted-runs index, one run per part, so that four runs overlap. A walk visits every key once,
// from the first. Run in a checkout of the original and in the tree.
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

/// <summary>Every key of a column visited once, by the source that serves it, against the rows per key.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DistinctWalkBenchmarks
{
    /// <summary>The column walked and the source that serves it.</summary>
    [Params(Walked.SortedInt64, Walked.RunsInt64, Walked.RunsUtf8)]
    public Walked Column { get; set; }

    /// <summary>The rows that share a key.</summary>
    [Params(1, 8, 64)]
    public int Dups { get; set; }

    /// <summary>A column and its source.</summary>
    public enum Walked
    {
        /// <summary>64-bit integers in key order, a sorted column.</summary>
        SortedInt64,

        /// <summary>64-bit integers spread over the parts, their sorted runs.</summary>
        RunsInt64,

        /// <summary>Strings of sixteen bytes spread over the parts, their sorted runs.</summary>
        RunsUtf8,
    }

    private const int Rows = 1 << 18;

    private const int Parts = 4;

    private const int PartRows = Rows / Parts;

    private static readonly string[] Names = ["sorted_i64", "sorted_utf8", "mixed_i64", "mixed_utf8"];

    private string _path = "";
    private VortexFile _file = null!;
    private KeyCursor _cursor = null!;

    /// <summary>Writes the file and opens the walk.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-distinct-{Guid.NewGuid():N}.vortex");
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(Names, [i64, utf8, i64, utf8], Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions
        {
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None
                .For("mixed_i64", IndexSpec.SortedRuns)
                .For("mixed_utf8", IndexSpec.SortedRuns),
        };

        for (int part = 0; part < Parts; part++)
        {
            await using VortexFileWriter writer = part == 0
                ? VortexFileWriter.Create(_path, schema, options)
                : await VortexFileWriter.AppendAsync(_path, options, CancellationToken.None);
            CanonicalArena arena = new CanonicalArena();
            int[] columns =
            [
                Integers(arena, i64, part, sorted: true),
                Strings(arena, utf8, part, sorted: true),
                Integers(arena, i64, part, sorted: false),
                Strings(arena, utf8, part, sorted: false),
            ];
            int root = arena.AddStruct(schema, PartRows, Validity.NonNullable, columns);
            using (RecordBatch batch = new RecordBatch(arena, root, (long)part * PartRows))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        _file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        (string column, KeySourceKind source) = Column switch
        {
            Walked.SortedInt64 => ("sorted_i64", KeySourceKind.SortedColumn),
            Walked.RunsInt64 => ("mixed_i64", KeySourceKind.SortedRuns),
            _ => ("mixed_utf8", KeySourceKind.SortedRuns),
        };
        _cursor = await _file.Keys(column).Distinct().WithSource(source).OpenAsync(CancellationToken.None);
        if (await Walk() != Rows / Dups)
        {
            throw new InvalidOperationException("The walk did not visit every key once.");
        }
    }

    /// <summary>Closes the walk and deletes the file.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cursor.DisposeAsync();
        await _file.DisposeAsync();
        System.IO.File.Delete(_path);
    }

    /// <summary>Every key, from the first, one step each.</summary>
    [Benchmark]
    public async Task<long> Walk()
    {
        long keys = 0;
        for (bool ok = await _cursor.SeekFirstAsync(CancellationToken.None); ok; ok = await _cursor.NextAsync(CancellationToken.None))
        {
            keys++;
        }

        return keys;
    }

    /// <summary>The key of a part's row: in key order across the parts, or spread over the whole range by every part.</summary>
    private long KeyOf(int part, int row, bool sorted) =>
        (sorted ? ((long)part * PartRows) + row : ((long)row * Parts) + part) / Dups;

    private int Integers(CanonicalArena arena, DType dtype, int part, bool sorted)
    {
        VortexBuffer buffer = arena.Allocate(PartRows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < PartRows; row++)
        {
            values[row] = KeyOf(part, row, sorted);
        }

        return arena.AddPrimitive(dtype, PartRows, Validity.NonNullable, PType.I64, buffer);
    }

    private int Strings(CanonicalArena arena, DType dtype, int part, bool sorted)
    {
        const int Length = 16;
        VortexBuffer heap = arena.Allocate(PartRows * Length, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(PartRows * 16, 16, out Span<byte> viewBytes);
        for (int row = 0; row < PartRows; row++)
        {
            Span<byte> value = heapBytes.Slice(row * Length, Length);
            Encoding.ASCII.GetBytes($"key-{KeyOf(part, row, sorted):D12}", value);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, Length);
            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], row * Length);
        }

        return arena.AddVarBinView(dtype, PartRows, Validity.NonNullable, views, [heap]);
    }
}
