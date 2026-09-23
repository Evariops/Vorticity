using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Writing;

/// <summary>
/// An append-only list that stops copying once it is long: for what the writer keeps per block --
/// a block's summary, its zone's string bounds -- over a file whose length it does not know, and
/// reads back at completion.
/// </summary>
/// <remarks>
/// Up to 64 entries it is a <see cref="List{T}"/>: one array, doubled by copying, and an object of
/// the same size, so a column of a few blocks costs what it did. Past that, a
/// <see cref="List{T}"/> keeps doubling, copying the whole list each time and leaving the old array
/// behind -- ten arrays for a column of 1 280 blocks, the last two past the large object threshold
/// -- while this one keeps the full array as its first segment and adds segments of 64, 128, 256
/// and then 512 entries, never copying one: under half the bytes at that length, none of them large.
/// Every array comes from the shared pools and goes back to them at <see cref="Release"/>, so a
/// process that writes file after file grows its lists in the same arrays.
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

    /// <summary>Null, the single array while short, then the array of segments.</summary>
    private object? _store;
    private int _count;

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
            return ref Unsafe.As<T[]>(_store!)[index];
        }

        (int segment, int offset) = Locate(index);
        return ref Unsafe.As<T[][]>(_store!)[segment][offset];
    }

    /// <summary>Appends <paramref name="item"/>.</summary>
    internal void Add(T item)
    {
        int index = _count;
        if (index < 1 << SingleShift)
        {
            T[]? single = Unsafe.As<T[]?>(_store);
            if (single is null || index == single.Length)
            {
                single = Grow(single, index);
                _store = single;
            }

            single[index] = item;
        }
        else
        {
            (int segment, int offset) = Locate(index);
            T[][] segments = index == 1 << SingleShift
                ? Segmented()
                : Unsafe.As<T[][]>(_store!);
            if (offset == 0)
            {
                if (segment == segments.Length)
                {
                    T[][] grown = ArrayPool<T[]>.Shared.Rent(segments.Length * 2);
                    segments.AsSpan().CopyTo(grown);
                    ArrayPool<T[]>.Shared.Return(segments, clearArray: true);
                    segments = grown;
                    _store = segments;
                }

                segments[segment] = ArrayPool<T>.Shared.Rent(Length(segment));
            }

            segments[segment][offset] = item;
        }

        _count = index + 1;
    }

    /// <summary>
    /// Gives every array back to the shared pools and empties the list; for a writer that is done
    /// with the file, whose next file then grows its lists in the same arrays.
    /// </summary>
    internal void Release()
    {
        object? store = _store;
        int count = _count;
        _store = null;
        _count = 0;
        if (store is null)
        {
            return;
        }

        if (count <= 1 << SingleShift)
        {
            Give(Unsafe.As<T[]>(store));
            return;
        }

        T[][] segments = Unsafe.As<T[][]>(store);
        for (int i = 0; i < segments.Length && segments[i] is { } segment; i++)
        {
            Give(segment);
        }

        ArrayPool<T[]>.Shared.Return(segments, clearArray: true);
    }

    /// <summary>The single array, twice as long, holding its <paramref name="count"/> entries; the old one goes back.</summary>
    /// <remarks>
    /// Below <see cref="PooledLength"/> the array is the list's own: the pools hand out nothing
    /// shorter, and a schema of a thousand one-block columns would take a thousand of their
    /// shortest arrays, four times the entries it needs and more arrays than they keep.
    /// </remarks>
    private static T[] Grow(T[]? single, int count)
    {
        int length = single is null ? FirstLength : single.Length * 2;
        T[] grown = length < PooledLength ? new T[length] : ArrayPool<T>.Shared.Rent(length);
        if (single is not null)
        {
            single.AsSpan(0, count).CopyTo(grown);
            Give(single);
        }

        return grown;
    }

    /// <summary>The shortest array the shared pools hand out, below which the list keeps its own.</summary>
    private const int PooledLength = 16;

    /// <summary>Returns <paramref name="array"/> to the shared pool when it came from there.</summary>
    private static void Give(T[] array)
    {
        if (array.Length >= PooledLength)
        {
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
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

    /// <summary>The full single array, now the first of the segments.</summary>
    private T[][] Segmented()
    {
        T[][] segments = ArrayPool<T[]>.Shared.Rent(8);
        segments[0] = Unsafe.As<T[]>(_store!);
        _store = segments;
        return segments;
    }

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
