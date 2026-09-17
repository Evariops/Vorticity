// What a composite key costs the writer (docs/10-indexes.md §6.5), next to a single column's runs.
//
// A composite key is never a default, so no throughput axis ever wrote one (IMPL-PLAN.md §1.11).
// Its builder is the sorted-runs builder over binary keys, and what it adds is the row encoding of
// the tuple: each fed range of the key columns sliced, encoded together, then interned. Three
// policies over the same two-column batches say how much of the price is the sort and how much the
// encoding.
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>A million rows written with no key, one column's runs, and a two-column key. A comparison: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class CompositeKeyWriteBenchmarks
{
    private const int Rows = 1 << 20;
    private const int Batch = 8_192;

    private static readonly string[] Countries = ["FR", "DE", "ES", "IT", "PT", "NL", "BE", "a-country-with-a-long-name"];

    private readonly DTypeArena _types = new DTypeArena();
    private DType _schema;
    private CanonicalArena[] _arenas = [];
    private int[] _roots = [];

    /// <summary>What the file is indexed with.</summary>
    [Params("none", "runs(n)", "key(country, n)")]
    public string Index { get; set; } = "none";

    [GlobalSetup]
    public void Setup()
    {
        DType utf8 = _types.Utf8(Nullability.NonNullable);
        DType i32 = _types.Primitive(PType.I32, Nullability.NonNullable);
        _schema = _types.Struct(["country", "n"], [utf8, i32], Nullability.NonNullable);
        int batches = Rows / Batch;
        _arenas = new CanonicalArena[batches];
        _roots = new int[batches];
        for (int b = 0; b < batches; b++)
        {
            CanonicalArena arena = new CanonicalArena();
            int start = b * Batch;
            _arenas[b] = arena;
            _roots[b] = arena.AddStruct(
                _schema, Batch, Validity.NonNullable, [Strings(arena, utf8, start), Ints(arena, i32, start)]);
        }
    }

    [Benchmark(Description = "write 1M rows to a sink that keeps nothing")]
    public async Task<long> Write()
    {
        IndexPolicy runs = IndexPolicy.SortedRuns;
        VortexWriteOptions options = new VortexWriteOptions
        {
            IndexBudgetPerMille = 1_000_000,
            Indexes = Index switch
            {
                "none" => WritePolicy.None,
                "runs(n)" => WritePolicy.None.For("n", runs),
                _ => WritePolicy.None.ForKey(["country", "n"], runs),
            },
            KeyEncoder = new RowKeyEncoder(RowSortField.Ascending),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(Stream.Null), _schema, options);
        for (int b = 0; b < _arenas.Length; b++)
        {
            RecordBatch batch = new RecordBatch(_arenas[b], _roots[b], (long)b * Batch);
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        WriteReport report = await writer.CompleteAsync(CancellationToken.None);
        return report.RowCount;
    }

    private static int Ints(CanonicalArena arena, DType dtype, int start)
    {
        VortexBuffer buffer = arena.Allocate(Batch * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
        for (int i = 0; i < Batch; i++)
        {
            values[i] = (int)((uint)(start + i) * 2654435761u % 100_000);
        }

        return arena.AddPrimitive(dtype, Batch, Validity.NonNullable, PType.I32, buffer);
    }

    private static int Strings(CanonicalArena arena, DType dtype, int start)
    {
        int heap = 0;
        for (int i = 0; i < Batch; i++)
        {
            string country = Countries[(start + i) * 7 % Countries.Length];
            heap += country.Length > 12 ? country.Length : 0;
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Batch * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        int offset = 0;
        for (int i = 0; i < Batch; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(Countries[(start + i) * 7 % Countries.Length]);
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, utf8.Length);
            if (utf8.Length <= 12)
            {
                utf8.CopyTo(view[4..]);
                continue;
            }

            utf8.AsSpan(0, 4).CopyTo(view[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
            utf8.CopyTo(dataBytes[offset..]);
            offset += utf8.Length;
        }

        return arena.AddVarBinView(dtype, Batch, Validity.NonNullable, views, [data]);
    }
}
