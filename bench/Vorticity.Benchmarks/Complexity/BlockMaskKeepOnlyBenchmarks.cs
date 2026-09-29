// What narrowing a block mask to one split after another costs, against the blocks of the mask.
//
// A mask of `Blocks` blocks of 64 rows is narrowed to each of its blocks in turn, as a minimum or
// a maximum narrows its scope to every split it decodes. Run in a checkout of the original and in
// the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;
using Vorticity.File;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A block mask narrowed to each of its blocks in turn, against its blocks.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class BlockMaskKeepOnlyBenchmarks
{
    /// <summary>Rows per block.</summary>
    private const int BlockRows = 64;

    /// <summary>Blocks of the mask, and splits it is narrowed to.</summary>
    [Params(4_096, 65_536)]
    public int Blocks { get; set; }

    private BlockMask _mask = null!;

    /// <summary>Makes the mask, and checks the last narrowing leaves one block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _mask = new BlockMask((long)Blocks * BlockRows, BlockRows);
        if (Narrow() != 1)
        {
            throw new InvalidOperationException("The mask was not narrowed to one block.");
        }
    }

    /// <summary>The mask narrowed to every block in turn.</summary>
    [Benchmark]
    public int Narrow()
    {
        for (long block = 0; block < Blocks; block++)
        {
            _mask.KeepOnly(new RowRange(block * BlockRows, (block + 1) * BlockRows));
        }

        return _mask.LiveCount;
    }
}
