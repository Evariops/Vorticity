// What the write path's hash tables pay for a column built to collide in them.
//
// A chunk of D distinct values probed into the writer's distinct table, D Bloom hashes added to
// the set a block's filter is built from, and D keys interned by the table a locating index keeps.
// `Forged` values are worked back from the hashes they are meant to have under fixed constants,
// all of them sharing their low bits, or all equal where a string is folded to one word before it
// is mixed: 64-bit integers through the inverse of the mix, strings of twelve bytes (inline in
// their view), sixteen and thirty-two by choosing one word from the others. Random values of the
// same shapes are the control. A table that cannot tell where a forged value lands pays the same
// for both; one that can walks a run as long as the values it holds at every insert. Run in a
// checkout of the original and in the tree, alternately.
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A chunk's distinct values probed into the writer's table, forged against random.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class WriteHashFloodBenchmarks
{
    /// <summary>The distinct values of the chunk.</summary>
    [Params(16_384, 65_536)]
    public int Distinct { get; set; }

    /// <summary>The values' shape.</summary>
    [Params(KeyShape.Int64, KeyShape.Inline12, KeyShape.Bytes16, KeyShape.Bytes32)]
    public KeyShape Key { get; set; }

    /// <summary>Whether the values are built to collide under the original constants.</summary>
    [Params(false, true)]
    public bool Forged { get; set; }

    /// <summary>A column's values.</summary>
    public enum KeyShape
    {
        /// <summary>64-bit integers.</summary>
        Int64,

        /// <summary>Strings of twelve bytes, held inline in their views.</summary>
        Inline12,

        /// <summary>Strings of sixteen bytes, out of line.</summary>
        Bytes16,

        /// <summary>Strings of thirty-two bytes, out of line.</summary>
        Bytes32,
    }

    private CanonicalArena _arena = null!;
    private int _node;
    private DistinctTable _table = null!;

    /// <summary>Builds the column and probes it once, so the table is rented at its size.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        _node = Key == KeyShape.Int64
            ? Integers(types.Primitive(PType.I64, Nullability.NonNullable))
            : Strings(_arena, types.Utf8(Nullability.NonNullable), Distinct, Key switch
            {
                KeyShape.Inline12 => 12,
                KeyShape.Bytes16 => 16,
                _ => 32,
            }, Forged);
        _table = DistinctTable.For(_arena.GetNode(_node))!;
        if (Probe() != Distinct)
        {
            throw new InvalidOperationException("The column's values are not all distinct.");
        }
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _table.Reset();
        _arena.Reset();
    }

    /// <summary>The chunk probed into the library's distinct table.</summary>
    [Benchmark]
    public int Probe()
    {
        _table.Reset();
        _table.Probe(_arena, _arena.GetNode(_node), 0, Distinct);
        return _table.Distinct;
    }

    private int Integers(DType dtype)
    {
        VortexBuffer buffer = _arena.Allocate(Distinct * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<ulong> values = MemoryMarshal.Cast<byte, ulong>(bytes);
        Random random = new Random(22);
        for (int i = 0; i < Distinct; i++)
        {
            values[i] = Forged ? Collisions.Unmix((ulong)(i + 1) << 32) : (ulong)random.NextInt64();
        }

        return _arena.AddPrimitive(dtype, Distinct, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>A string column of <paramref name="count"/> values of <paramref name="length"/> bytes.</summary>
    internal static int Strings(CanonicalArena arena, DType dtype, int count, int length, bool forged)
    {
        VortexBuffer heap = arena.Allocate(Math.Max(count * length, 1), 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        Random random = new Random(22);
        for (int i = 0; i < count; i++)
        {
            Span<byte> value = heapBytes.Slice(i * length, length);
            if (forged)
            {
                Collisions.Write(value, (ulong)(i + 1));
            }
            else
            {
                random.NextBytes(value);
            }

            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], i * length);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [heap]);
    }
}

/// <summary>D Bloom hashes added to the set a filter is built from, forged against random.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class BloomSetFloodBenchmarks
{
    /// <summary>The distinct hashes.</summary>
    [Params(16_384, 65_536)]
    public int Distinct { get; set; }

    /// <summary>Whether the hashes share their low 32 bits.</summary>
    [Params(false, true)]
    public bool Forged { get; set; }

    private ulong[] _hashes = [];
    private HashSet64 _set = null!;

    /// <summary>Draws the hashes and adds them once, so the set has grown to its size.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _hashes = new ulong[Distinct];
        Random random = new Random(22);
        for (int i = 0; i < Distinct; i++)
        {
            _hashes[i] = Forged ? (ulong)(i + 1) << 32 : (ulong)random.NextInt64() | 1;
        }

        _set = new HashSet64();
        if (Add() != Distinct)
        {
            throw new InvalidOperationException("The hashes are not all distinct.");
        }
    }

    /// <summary>Gives the set's table back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _set.Dispose();

    /// <summary>The hashes added to the library's set.</summary>
    [Benchmark]
    public int Add()
    {
        _set.Clear();
        foreach (ulong hash in _hashes)
        {
            _set.Add(hash);
        }

        return _set.Count;
    }
}

/// <summary>
/// A string column of D distinct keys fed to the builder of a sorted-runs index, which interns every
/// row's key in the table it keeps per chunk, forged against random.
/// </summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class IndexKeyFloodBenchmarks
{
    /// <summary>The distinct keys.</summary>
    [Params(16_384, 65_536)]
    public int Distinct { get; set; }

    /// <summary>A key's bytes.</summary>
    [Params(16, 32)]
    public int Length { get; set; }

    /// <summary>Whether the keys are built to collide under the original constants.</summary>
    [Params(false, true)]
    public bool Forged { get; set; }

    private CanonicalArena _arena = null!;
    private int _node;
    private Indexes.KeyLayout _layout;
    private bool _utf8;

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DType dtype = new DTypeArena().Utf8(Nullability.NonNullable);
        _node = WriteHashFloodBenchmarks.Strings(_arena, dtype, Distinct, Length, Forged);
        if (!KeyIndexBuilder.Supports(dtype, out _layout, out _utf8, out string? reason))
        {
            throw new InvalidOperationException(reason);
        }
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The column fed to a new builder, as the writer feeds a chunk's batch.</summary>
    [Benchmark]
    public int Accumulate()
    {
        using KeyIndexBuilder builder = new KeyIndexBuilder(rows: true, _layout, _utf8);
        builder.Accumulate(_arena, _node, 0, Distinct);
        return builder.SegmentEntries;
    }
}

/// <summary>Values worked back from the hashes the original constants give them.</summary>
internal static class Collisions
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong Spread = 0xBF58476D1CE4E5B9UL;

    /// <summary>The value the original mix sends to <paramref name="hash"/>.</summary>
    internal static ulong Unmix(ulong hash)
    {
        hash ^= hash >> 32;
        hash *= Inverse(Spread);
        hash ^= (hash >> 29) ^ (hash >> 58);
        return hash * Inverse(Golden);
    }

    /// <summary>
    /// The <paramref name="index"/>th of the strings of <paramref name="value"/>'s length that the
    /// original fold sends to one word: one word of each is the index, one is solved for.
    /// </summary>
    internal static void Write(Span<byte> value, ulong index)
    {
        Span<ulong> words = stackalloc ulong[4];
        switch (value.Length)
        {
            case 12:
                // head ^ tail * Golden, the tail being the last four bytes.
                BinaryPrimitives.WriteUInt64LittleEndian(value, 0x5EED_0000_0000_0001UL ^ ((index & uint.MaxValue) * Golden));
                BinaryPrimitives.WriteUInt32LittleEndian(value[8..], (uint)index);
                return;

            case 16:
                // low ^ high * Spread.
                words[1] = index;
                words[0] = 0x5EED_0000_0000_0002UL ^ (index * Spread);
                break;

            default:
                // a * Golden ^ rotl(c * Spread, 31), with b and d fixed.
                words[0] = index;
                words[1] = 0x0123_4567_89AB_CDEFUL;
                words[2] = BitOperations.RotateRight(0x5EED_0000_0000_0003UL ^ (index * Golden), 31) * Inverse(Spread);
                words[3] = 0xFEDC_BA98_7654_3210UL;
                break;
        }

        for (int i = 0; i < value.Length / 8; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(value[(i * 8)..], words[i]);
        }
    }

    /// <summary>The inverse of an odd multiplier modulo 2^64, by Newton's iteration.</summary>
    private static ulong Inverse(ulong odd)
    {
        ulong inverse = odd;
        for (int i = 0; i < 5; i++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }
}
