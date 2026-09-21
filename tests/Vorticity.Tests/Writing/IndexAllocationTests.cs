// One row per index kind, so that a builder that allocates per row is caught.
//
// THE FIGURE IS THE SLOPE ABOVE THE WRITER'S OWN. Each kind writes the same two-column file at N
// and at 2N rows; what is held is the allocation that grows with the rows between the two, minus
// what the same write grows without an index -- a per-file cost cancels out, a per-row one does
// not. The writer's own slope is 4,85 B/row, and above it:
//
//   auto            0,0    the default: nothing per row
//   bloom           0,5    each generation's block filters copied once, payloads in a pooled arena
//   ngram-bloom     0,25   the same, over trigrams
//   postings        2,5    the payloads' compression; the chunk's table and arrays are kept or rented
//   ngram-postings  2,4    the same, over trigrams
//   sorted-runs     6,6    the same over one entry per row -- the one chunk in memory, in flux,
//                          is held in buffers that grow once per file, to the largest chunk
//
// The locating kinds keep the key table, its log, the sort's arrays and the payload copies from
// one chunk to the next: reallocated per chunk, they would pass their ceilings. A kind whose
// slope passes its ceiling allocates per row something the paragraph above does not account for.
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Indexes;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <remarks>
/// Shares <see cref="AllocationCollection"/>: the pools are process-global, and a neighbour draining
/// them charges a rent here -- the three kinds that failed in a full run passed alone.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class IndexAllocationTests
{
    private const int Rows = 8 * 8_192;
    private const int Batch = 8_192;

    public static TheoryData<string, double> Kinds() => new()
    {
        { "auto", 1.0 },
        { "bloom", 2.0 },
        { "ngram-bloom", 2.0 },
        { "postings", 4.0 },
        { "ngram-postings", 4.0 },
        { "sorted-runs", 9.0 },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EachKindAllocatesPerRowOnlyWhatItsDesignSays(string kind, double ceiling)
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        double own = await Slope(WritePolicy.None);
        double slope = await Slope(Policy(kind));
        double above = slope - own;

        Assert.True(
            above <= ceiling,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{kind}: {above:F2} B/row above the writer's own {own:F2}, over its {ceiling:F1}"));
    }

    private static async Task<double> Slope(WritePolicy policy)
    {
        long small = await Floor(policy, Rows);
        long large = await Floor(policy, 2 * Rows);
        return (double)(large - small) / Rows;
    }

    private static WritePolicy Policy(string kind) => kind switch
    {
        "none" => WritePolicy.None,
        "auto" => WritePolicy.Auto,
        "bloom" => WritePolicy.None.WithDefault(IndexPolicy.Bloom()),
        "ngram-bloom" => WritePolicy.None.For("text", IndexPolicy.NgramBloom()),
        "postings" => WritePolicy.None.WithDefault(IndexPolicy.Postings),
        "ngram-postings" => WritePolicy.None.For("text", IndexPolicy.NgramPostings()),
        _ => WritePolicy.None.WithDefault(IndexPolicy.SortedRuns),
    };

    /// <summary>The floor of several writes, after warm-ups, so the pool is in its steady state.</summary>
    private static async Task<long> Floor(WritePolicy policy, int rows)
    {
        for (int warm = 0; warm < 3; warm++)
        {
            await WriteAsync(policy, rows);
        }

        long floor = long.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            floor = Math.Min(floor, await WriteAsync(policy, rows));
        }

        return floor;
    }

    private static async Task<long> WriteAsync(WritePolicy policy, int rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["id", "text"], [i64, utf8], Nullability.NonNullable);

        // The batches are built before the measurement: what is held is the writer's allocation.
        (int Root, int Count)[] batches = new (int, int)[(rows + Batch - 1) / Batch];
        for (int b = 0; b < batches.Length; b++)
        {
            int start = b * Batch;
            int count = Math.Min(Batch, rows - start);
            int[] columns = [Longs(arena, i64, start, count), Strings(arena, utf8, start, count)];
            batches[b] = (arena.AddStruct(schema, count, Validity.NonNullable, columns), count);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(System.IO.Stream.Null), schema,
            new VortexWriteOptions { WritePolicy = policy, IndexBudgetPerMille = 1_000_000 }))
        {
            long offset = 0;
            foreach ((int root, int count) in batches)
            {
                RecordBatch batch = new RecordBatch(arena, root, offset);
                await writer.WriteAsync(batch, CancellationToken.None);
                offset += count;
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Longs(CanonicalArena arena, DType dtype, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = (long)((uint)(start + i) * 2654435761u % 1_000);
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Strings(CanonicalArena arena, DType dtype, int start, int count)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(
                "t" + ((uint)(start + i) * 40503u % 1_000).ToString("D4", CultureInfo.InvariantCulture));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
