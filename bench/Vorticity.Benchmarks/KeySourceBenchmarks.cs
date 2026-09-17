// Which key-only source is cheaper for a Distinct() cursor: postings or the dictionaries.
//
// docs/12-index-reads.md §3 orders a walk of keys "cheapest first" -- the sorted column, the
// postings, the dictionaries, the sorted runs -- and step 15 shipped that order without a number
// behind the middle of it, because a file carried one policy per column and so never both
// (IMPL-PLAN.md §1.10). It can: `Auto` writes the dictionary probe, and an index added after the
// fact keeps the old directory's entries of other kinds. So one file here carries all three for the
// same column, and each benchmark forces one source.
//
// THE OPEN IS INSIDE THE MEASUREMENT. What distinguishes the sources is what opening them reads --
// a postings run's keys segment, a dictionary chunk's values child, a sorted run's keys segment --
// so a benchmark that opened the cursor once in its setup would compare the steps and miss the
// difference.
//
// MEASURED ON 2026-09-17 (1 M rows, ~27 runs), the order holds on both axes:
//
//   distinct keys   source        walk        seek + 100 keys   allocated
//   100             postings        149 us       129 us          0.2 MB
//                   dictionary      280 us       250 us          0.2 MB
//                   sorted runs   7 370 us     6 436 us         18.6 MB
//   10 000          postings     14 645 us     2 567 us          2.7 MB
//                   dictionary   36 297 us    23 843 us         11.2 MB
//                   sorted runs  25 701 us     8 331 us         19.0 MB
//
// A seek over sorted runs loads a segment in every run, and at the default 65 536 entries a run
// of a chunk is one segment: the whole index, whatever the seek. That is the design's price and
// the reason the runs come last for a walk of keys.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>A walk and a seek of distinct keys, per forced source. A comparison: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class KeySourceBenchmarks
{
    private const int Rows = 1 << 20;
    private const string Column = "k";

    private string _path = string.Empty;
    private FilterLiteral _middle;

    /// <summary>Distinct keys in the column: a dictionary's column at both ends.</summary>
    [Params(100, 10_000)]
    public int Cardinality { get; set; } = 100;

    /// <summary>The source the cursor is forced onto.</summary>
    [Params(KeySourceKind.Postings, KeySourceKind.Dictionary, KeySourceKind.SortedRuns)]
    public KeySourceKind Source { get; set; } = KeySourceKind.Postings;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-keysource-{Cardinality}-{Guid.NewGuid():N}.vortex");
        _middle = FilterLiteral.From(Key(Cardinality / 2));
        WriteAsync().AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "distinct walk, open included")]
    public async Task<long> Walk()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        await using KeyCursor cursor = await file.Keys(Column).Distinct().WithSource(Source).OpenAsync();
        long keys = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextKeyAsync())
        {
            keys++;
        }

        return keys;
    }

    [Benchmark(Description = "seek the middle key, then 100 keys, open included")]
    public async Task<long> Seek()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        await using KeyCursor cursor = await file.Keys(Column).Distinct().WithSource(Source).OpenAsync();
        long keys = 0;
        for (bool ok = await cursor.SeekAsync(_middle, SeekOp.AtOrAfter); ok && keys < 100; ok = await cursor.NextKeyAsync())
        {
            keys++;
        }

        return keys;
    }

    private static string Key(int value) => "key-" + value.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// The file, written under `Auto` (the dictionary probe), then given postings and sorted runs
    /// after the fact; its plan is checked to hold all three, so a benchmark never measures a refusal.
    /// </summary>
    private async ValueTask WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct([Column], [utf8], Nullability.NonNullable);
        await using (VortexFileWriter writer = VortexFileWriter.Create(_path, schema, new VortexWriteOptions()))
        {
            const int Batch = 8_192;
            for (int start = 0; start < Rows; start += Batch)
            {
                CanonicalArena arena = new CanonicalArena();
                int root = arena.AddStruct(schema, Batch, Validity.NonNullable, [Views(arena, utf8, start, Batch)]);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        VortexWriteOptions unbounded = new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 };
        await VortexFileIndexer.AppendIndexesAsync(_path, WritePolicy.None.For(Column, IndexPolicy.Postings), unbounded);
        await VortexFileIndexer.AppendIndexesAsync(_path, WritePolicy.None.For(Column, IndexPolicy.SortedRuns), unbounded);

        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        foreach (KeySourceKind kind in new[] { KeySourceKind.Postings, KeySourceKind.Dictionary, KeySourceKind.SortedRuns })
        {
            KeyPlan plan = await file.Keys(Column).Distinct().WithSource(kind).ExplainAsync();
            if (plan.Source != kind)
            {
                throw new InvalidOperationException(
                    $"The benchmark file does not carry {kind}: {string.Join("; ", plan.Rejected)}");
            }
        }
    }

    private int Views(CanonicalArena arena, DType dtype, int start, int count)
    {
        // Every key is ten bytes, so every view is inline and there is no data buffer to fill.
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            int value = (int)((uint)(start + i) * 2654435761u % (uint)Cardinality);
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, 10);
            Encoding.ASCII.GetBytes(Key(value), view[4..]);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
