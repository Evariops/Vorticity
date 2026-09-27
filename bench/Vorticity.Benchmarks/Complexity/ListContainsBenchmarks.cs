// What a list membership test pays for list views that overlap.
//
// One batch of 2,048 list rows of W 64-bit elements each, their views either disjoint, as a writer
// lays lists, or sliding over one element at a time, which list views may legally do: the rows then
// name W times more elements than the window they share holds. The value sought appears once in a
// thousand elements. `Original` searches each row's elements for a match (`ListContainsBefore.cs`);
// `Library` is the library's test, which answers overlapping rows from a count of the matches.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A list membership test over one batch, against how much the rows' views overlap.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ListContainsBenchmarks
{
    /// <summary>The elements of a row.</summary>
    [Params(16, 64, 256, 4_096)]
    public int Width { get; set; }

    /// <summary>Whether each row's view starts one element after the row before's.</summary>
    [Params(false, true)]
    public bool Sliding { get; set; }

    private const int Rows = 2_048;

    private CanonicalArena _arena = null!;
    private int _node;
    private byte[] _states = [];
    private FilterLiteral _sought;

    /// <summary>Builds the batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType list = types.List(i64, Nullability.NonNullable);
        _arena = new CanonicalArena();
        int elementCount = Sliding ? Rows + Width : Rows * Width;
        VortexBuffer elementBuffer = _arena.Allocate(elementCount * sizeof(long), sizeof(long), out Span<byte> elementBytes);
        Span<long> element = MemoryMarshal.Cast<byte, long>(elementBytes);
        for (int i = 0; i < elementCount; i++)
        {
            element[i] = i % 1_000 == 999 ? 7 : i % 997;
        }

        int elements = _arena.AddPrimitive(i64, elementCount, Validity.NonNullable, PType.I64, elementBuffer);
        VortexBuffer offsets = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        VortexBuffer sizes = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        Span<long> offset = MemoryMarshal.Cast<byte, long>(offsetBytes);
        Span<long> size = MemoryMarshal.Cast<byte, long>(sizeBytes);
        for (int row = 0; row < Rows; row++)
        {
            offset[row] = Sliding ? row : (long)row * Width;
            size[row] = Width;
        }

        _node = _arena.AddListView(list, Rows, Validity.NonNullable, elements, offsets, PType.I64, sizes, PType.I64);
        _states = new byte[Rows];
        _sought = FilterLiteral.From(7L);
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>Each row's elements searched.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        ListContainsBefore.Views(_arena, _arena.GetNode(_node), ValidityMask.NonNullable, _sought, _states);
        return Trilean.CountTrue(_states);
    }

    /// <summary>The library's test.</summary>
    [Benchmark]
    public int Library()
    {
        ListKernels.Contains(_arena, _node, _sought, _states);
        return Trilean.CountTrue(_states);
    }
}
