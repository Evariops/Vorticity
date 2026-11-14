using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Buffers;

/// <summary>
/// <see cref="Length"/> elements of <typeparamref name="T"/> laid in a block of bytes the pool lends
/// (<see cref="Blocks"/>): what a rented <c>T[]</c> was, with the bytes as the one unit the pool keeps
/// and counts, so that a block one table of <c>long</c> gives back serves the next column of
/// <c>double</c>.
/// </summary>
/// <remarks>
/// <para>
/// A struct a field holds and an <c>await</c> crosses, read as an array is: by index, by
/// <see cref="Length"/>, through a span, under <c>fixed</c>. A loop that reads it many times takes its
/// span first, as it would of an array whose bounds it wants checked once.
/// </para>
/// <para>
/// Its length is the one asked for, never the block's rounded size: a table whose mask is its length
/// minus one keeps that mask, whatever the size of its element. <see cref="Capacity"/> says how far
/// the block goes, and <see cref="Blocks.Resize{T}"/> grows within it before it rents again.
/// </para>
/// <para>
/// A copy of the struct is a second view of the same bytes, as a second reference to an array is:
/// once the block is given back, neither may be read.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type, which holds no reference: the collector never reads a block.</typeparam>
[DebuggerDisplay("Block<{typeof(T).Name,nq}>[{Length}]")]
internal readonly struct Block<T>
    where T : unmanaged
{
    private readonly byte[]? _bytes;
    private readonly int _length;

    /// <summary>A view of <paramref name="length"/> elements over <paramref name="bytes"/>, which hold them.</summary>
    internal Block(byte[] bytes, int length)
    {
        Debug.Assert((long)length * Unsafe.SizeOf<T>() <= bytes.Length, "The bytes hold the elements.");
        _bytes = bytes;
        _length = length;
    }

    /// <summary>The empty block, which nothing was rented for: the default.</summary>
    internal static Block<T> Empty => default;

    /// <summary>The elements the block holds: the length it was rented at.</summary>
    public int Length => _length;

    /// <summary>Whether it holds no element.</summary>
    public bool IsEmpty => _length == 0;

    /// <summary>The elements its bytes have room for, at least <see cref="Length"/>.</summary>
    public int Capacity => _bytes is null ? 0 : _bytes.Length / Unsafe.SizeOf<T>();

    /// <summary>The bytes behind it, which the pool takes back: for an API that reads or writes a byte array, and for <see cref="Blocks"/>.</summary>
    internal byte[]? Bytes => _bytes;

    /// <summary>Element <paramref name="index"/>.</summary>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or not below <see cref="Length"/>.</exception>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_length)
            {
                ThrowIndex();
            }

            return ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!)), (nint)(uint)index);
        }
    }

    /// <summary>Element <paramref name="index"/>, an unsigned index as an array takes one.</summary>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is not below <see cref="Length"/>.</exception>
    public ref T this[uint index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (index >= (uint)_length)
            {
                ThrowIndex();
            }

            return ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!)), (nint)index);
        }
    }

    /// <summary>Element <paramref name="index"/>, an unsigned index as an array takes one.</summary>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is not below <see cref="Length"/>.</exception>
    public ref T this[ulong index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (index >= (uint)_length)
            {
                ThrowIndex();
            }

            return ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!)), (nint)index);
        }
    }

    /// <summary>Every element, as a span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan() =>
        _bytes is null ? default : MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes)), _length);

    /// <summary>The elements from <paramref name="start"/> on, as a span.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative or past <see cref="Length"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan(int start)
    {
        if ((uint)start > (uint)_length)
        {
            ThrowRange();
        }

        return _bytes is null
            ? default
            : MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes)), (nint)(uint)start), _length - start);
    }

    /// <summary><paramref name="length"/> elements from <paramref name="start"/>, as a span.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range does not lie within <see cref="Length"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan(int start, int length)
    {
        if ((ulong)(uint)start + (uint)length > (uint)_length)
        {
            ThrowRange();
        }

        return _bytes is null
            ? default
            : MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes)), (nint)(uint)start), length);
    }

    /// <summary>A reference to element 0, or a null reference for an empty block: what <c>fixed</c> pins.</summary>
    public ref T GetPinnableReference() =>
        ref _length == 0 ? ref Unsafe.NullRef<T>() : ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!));

    /// <summary>A reference to element 0, where it would lie for an empty block that has bytes; for a vector load from a computed offset.</summary>
    /// <exception cref="NullReferenceException">The block is the default, with no bytes.</exception>
    public ref T GetReference() => ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!));

    /// <summary>The same bytes seen as <paramref name="length"/> elements: within <see cref="Capacity"/>, without copying.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or past <see cref="Capacity"/>.</exception>
    internal Block<T> WithLength(int length)
    {
        if ((uint)length > (uint)Capacity)
        {
            ThrowRange();
        }

        return _bytes is null ? default : new Block<T>(_bytes, length);
    }

    /// <summary>Whether <paramref name="other"/> is a view of the same bytes.</summary>
    internal bool SameBytes(Block<T> other) => ReferenceEquals(_bytes, other._bytes);

    /// <summary>Zeroes every element.</summary>
    public void Clear() => AsSpan().Clear();

    /// <summary>Sets every element to <paramref name="value"/>.</summary>
    public void Fill(T value) => AsSpan().Fill(value);

    /// <summary>Copies every element to <paramref name="destination"/>.</summary>
    public void CopyTo(Span<T> destination) => AsSpan().CopyTo(destination);

    /// <summary>The elements in order, as a span's.</summary>
    public Span<T>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    /// <summary>Every element, as a span.</summary>
    public static implicit operator Span<T>(Block<T> block) => block.AsSpan();

    /// <summary>Every element, as a read-only span.</summary>
    public static implicit operator ReadOnlySpan<T>(Block<T> block) => block.AsSpan();

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex() => throw new IndexOutOfRangeException();

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRange() => throw new ArgumentOutOfRangeException();
}

/// <summary>What a block of bytes offers that a block of other elements cannot: memory over its array.</summary>
internal static class ByteBlocks
{
    /// <summary>Every byte, as memory over the block's array.</summary>
    internal static Memory<byte> AsMemory(this Block<byte> block) =>
        block.Bytes is { } bytes ? new Memory<byte>(bytes, 0, block.Length) : default;

    /// <summary><paramref name="length"/> bytes from <paramref name="start"/>, as memory over the block's array.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range does not lie within the block's length.</exception>
    internal static Memory<byte> AsMemory(this Block<byte> block, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((ulong)(uint)start + (uint)length, (ulong)(uint)block.Length);
        return block.Bytes is { } bytes ? new Memory<byte>(bytes, start, length) : default;
    }

    /// <summary>The bytes from <paramref name="start"/> on, as memory over the block's array.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is past the block's length.</exception>
    internal static Memory<byte> AsMemory(this Block<byte> block, int start) => block.AsMemory(start, block.Length - start);

    /// <summary>The array behind a block of bytes, from its first byte: for an API that takes an array, an offset and a count.</summary>
    internal static byte[] Array(this Block<byte> block) => block.Bytes ?? System.Array.Empty<byte>();
}
