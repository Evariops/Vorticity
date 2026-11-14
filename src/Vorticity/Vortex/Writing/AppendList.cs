using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// An append-only list that stops copying once it is long: for what the writer keeps per block --
/// a block's summary, its zone's string bounds -- over a file whose length it does not know, and
/// reads back at completion.
/// </summary>
/// <remarks>
/// <para>
/// Up to 64 entries it is a <see cref="List{T}"/>: one array, doubled by copying, and an object of
/// the same size, so a column of a few blocks costs what it did. Past that, a
/// <see cref="List{T}"/> keeps doubling, copying the whole list each time and leaving the old array
/// behind -- ten arrays for a column of 1 280 blocks, the last two past the large object threshold
/// -- while this one keeps the full array as its first segment and adds segments of 64, 128, 256
/// and then 512 entries, never copying one: under half the bytes at that length, none of them large.
/// </para>
/// <para>
/// Entries that hold no reference live in blocks of the pool (<see cref="Blocks"/>), given back at
/// <see cref="Release"/>, so that a process that writes file after file grows its lists in the same
/// bytes. Entries that hold one live in arrays of the list's own, which no block of bytes can be:
/// <see cref="Release"/> keeps the single array for the next file the writer writes, and lets the
/// segments go.
/// </para>
/// </remarks>
/// <typeparam name="T">The entry.</typeparam>
internal sealed class AppendList<T> : IReadOnlyList<T>
{
    /// <summary>The entries the single array grows to before the list turns to segments.</summary>
    private const int SingleShift = 6;

    /// <summary>Segments stop doubling at 1 &lt;&lt; this many entries, below the large object threshold for any entry a writer keeps.</summary>
    private const int LastShift = 9;

    /// <summary>The entries the single array starts with.</summary>
    private const int FirstLength = 4;

    /// <summary>
    /// Null, the single array while short, then the array of segments: arrays of entries, or the
    /// bytes of blocks when <see cref="InBlocks"/>.
    /// </summary>
    private object? _store;
    private int _count;

    /// <summary>Whether the entries hold no reference, and so live in blocks of the pool: a constant to the compiler.</summary>
    private static bool InBlocks => !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <inheritdoc/>
    public int Count => _count;

    /// <inheritdoc/>
    public T this[int index] => At(index);

    /// <summary>Entry <paramref name="index"/>, by reference, for what a closed block still records.</summary>
    internal ref T At(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            ThrowIndex(index, _count);
        }

        // The count says which form the store is in, not its type: an array of arrays of a
        // reference type is itself an array of that type's base.
        if (_count <= 1 << SingleShift)
        {
            return ref Entry(_store!, index);
        }

        (int segment, int offset) = Locate(index);
        return ref Entry(Segment(_store!, segment), offset);
    }

    /// <summary>Appends <paramref name="item"/>.</summary>
    internal void Add(T item)
    {
        int index = _count;
        if (index < 1 << SingleShift)
        {
            object? single = _store;
            if (single is null || index == Capacity(single))
            {
                single = Grow(single, index);
                _store = single;
            }

            Entry(single, index) = item;
        }
        else
        {
            (int segment, int offset) = Locate(index);
            object segments = index == 1 << SingleShift ? Segmented() : _store!;
            if (offset == 0)
            {
                segments = AddSegment(segments, segment);
                _store = segments;
            }

            Entry(Segment(segments, segment), offset) = item;
        }

        _count = index + 1;
    }

    /// <summary>
    /// Empties the list, for a writer that is done with the file. Blocks go back to the pool. Arrays
    /// of the list's own leave the single one, its references cleared, for the next file's list to
    /// grow in, and the segments to the collector.
    /// </summary>
    internal void Release()
    {
        object? store = _store;
        int count = _count;
        _count = 0;
        if (store is null)
        {
            return;
        }

        if (InBlocks)
        {
            _store = null;
            if (count <= 1 << SingleShift)
            {
                Blocks.ReturnBytes(Unsafe.As<byte[]>(store));
                return;
            }

            foreach (byte[]? segment in Unsafe.As<byte[]?[]>(store))
            {
                Blocks.ReturnBytes(segment);
            }

            return;
        }

        T[] single = count <= 1 << SingleShift ? Unsafe.As<T[]>(store) : Unsafe.As<T[][]>(store)[0];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            single.AsSpan(0, Math.Min(count, single.Length)).Clear();
        }

        _store = single;
    }

    /// <summary>Entry <paramref name="offset"/> of an array of the store.</summary>
    private static ref T Entry(object array, int offset)
    {
        if (!InBlocks)
        {
            return ref Unsafe.As<T[]>(array)[offset];
        }

        byte[] bytes = Unsafe.As<byte[]>(array);
        if ((uint)offset >= (uint)(bytes.Length / Unsafe.SizeOf<T>()))
        {
            ThrowIndex(offset, bytes.Length / Unsafe.SizeOf<T>());
        }

        return ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(bytes)), offset);
    }

    /// <summary>The entries an array of the store has room for.</summary>
    private static int Capacity(object array) =>
        InBlocks ? Unsafe.As<byte[]>(array).Length / Unsafe.SizeOf<T>() : Unsafe.As<T[]>(array).Length;

    /// <summary>Segment <paramref name="segment"/> of the array of segments.</summary>
    private static object Segment(object segments, int segment) =>
        InBlocks ? Unsafe.As<byte[][]>(segments)[segment] : Unsafe.As<T[][]>(segments)[segment];

    /// <summary>An array of the store for <paramref name="length"/> entries: a block of the pool, or the list's own.</summary>
    private static object NewArray(int length) =>
        InBlocks ? Blocks.Rent<byte>(length * Unsafe.SizeOf<T>()).Array() : new T[length];

    /// <summary>The single array, twice as long, holding its <paramref name="count"/> entries; a block it leaves goes back.</summary>
    private static object Grow(object? single, int count)
    {
        object grown = NewArray(single is null ? FirstLength : Capacity(single) * 2);
        if (single is null)
        {
            return grown;
        }

        if (InBlocks)
        {
            Unsafe.As<byte[]>(single).AsSpan(0, count * Unsafe.SizeOf<T>()).CopyTo(Unsafe.As<byte[]>(grown));
            Blocks.ReturnBytes(Unsafe.As<byte[]>(single));
        }
        else
        {
            Unsafe.As<T[]>(single).AsSpan(0, count).CopyTo(Unsafe.As<T[]>(grown));
        }

        return grown;
    }

    /// <summary>The full single array, now the first of the segments; the array of them is the list's own.</summary>
    private object Segmented()
    {
        object single = _store!;
        if (InBlocks)
        {
            byte[][] blocks = new byte[8][];
            blocks[0] = Unsafe.As<byte[]>(single);
            _store = blocks;
            return blocks;
        }

        T[][] segments = new T[8][];
        segments[0] = Unsafe.As<T[]>(single);
        _store = segments;
        return segments;
    }

    /// <summary>Segment <paramref name="segment"/> made, the array of segments doubled when it is full; returns that array.</summary>
    private static object AddSegment(object segments, int segment)
    {
        if (InBlocks)
        {
            byte[][] blocks = Unsafe.As<byte[][]>(segments);
            if (segment == blocks.Length)
            {
                Array.Resize(ref blocks, blocks.Length * 2);
            }

            blocks[segment] = Unsafe.As<byte[]>(NewArray(Length(segment)));
            return blocks;
        }

        T[][] arrays = Unsafe.As<T[][]>(segments);
        if (segment == arrays.Length)
        {
            Array.Resize(ref arrays, arrays.Length * 2);
        }

        arrays[segment] = new T[Length(segment)];
        return arrays;
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return At(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The entries segment <paramref name="segment"/> holds: 64, 64, 128, 256, then 512.</summary>
    /// <remarks>The doubling is bounded before it is shifted: a shift past 31 wraps rather than saturates.</remarks>
    private static int Length(int segment) =>
        segment <= LastShift - SingleShift
            ? (1 << SingleShift) << Math.Max(segment - 1, 0)
            : 1 << LastShift;

    /// <summary>The segment an index falls in once the list is in segments, and where in it.</summary>
    /// <remarks>
    /// Segment 0 is the single array; doubling segment <c>k</c> starts at <c>64 · 2^(k - 1)</c>,
    /// where the index's 64ths have <c>k - 1</c> for their base-2 logarithm; past them the segments
    /// are all 512 long.
    /// </remarks>
    private static (int Segment, int Offset) Locate(int index)
    {
        if (index < 1 << SingleShift)
        {
            return (0, index);
        }

        if (index < 1 << LastShift)
        {
            int segment = BitOperations.Log2((uint)(index >> SingleShift)) + 1;
            return (segment, index - ((1 << SingleShift) << (segment - 1)));
        }

        return ((index >> LastShift) + (LastShift - SingleShift), index & ((1 << LastShift) - 1));
    }

    [DoesNotReturn]
    private static void ThrowIndex(int index, int count) =>
        throw new ArgumentOutOfRangeException(
            nameof(index), index, $"The list holds {count} entries.");
}
