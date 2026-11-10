// Audit VW-06: what hashed postings cost as the writer feeds them, each row's value hashed and its hash placed in its
// block's bucket. 327,680 rows of a column of identifiers, longs drawn at random, fed by blocks of 8,192; the buckets kept
// in memory, the feed alone measured.
using System;
using System.IO;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Hashed postings fed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditHashedPostingsBenchmarks
{
    private const int Rows = 327_680;
    private const int Block = 8_192;

    private CanonicalArena _arena = null!;
    private int _node;
    private KeyLayout _layout;
    private string _directory = string.Empty;

    /// <summary>Draws the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(47);
        DType i64 = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        if (!KeyIndexBuilder.Supports(i64, out _layout, out _, out _))
        {
            throw new InvalidOperationException("Longs are keyed.");
        }

        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.NextInt64();
        }

        _node = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        _directory = Path.Combine(Path.GetTempPath(), $"hashed-postings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        Feed();
    }

    /// <summary>Removes the scratch directory.</summary>
    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>The column fed a block at a time; the rows fed.</summary>
    [Benchmark]
    public int Feed()
    {
        using RunScratch scratch = new RunScratch(memoryBudget: 256 << 20, _directory);
        using KeyIndexBuilder builder = KeyIndexBuilder.ForHashes(_layout, KeyRunOptions.DefaultSegmentEntries);
        builder.Scratch = scratch;
        builder.BlockRows = Block;
        builder.Start(0, 0);
        for (int start = 0; start < Rows; start += Block)
        {
            builder.Accumulate(_arena, _node, start, Block);
            builder.CloseBlock();
        }

        return Rows;
    }
}
