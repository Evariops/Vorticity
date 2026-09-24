using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Buffers;

/// <summary>
/// A non-owning, aligned view over a contiguous run of bytes. It is the single shape the reader
/// works in, whatever the bytes came from: memory-mapped pages, aligned native memory, or a
/// pinned managed array.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one pointer plus two 32-bit fields — 16 bytes on a 64-bit host. It deliberately holds
/// a raw pointer rather than a <see cref="ReadOnlySpan{T}"/>: the reader stores buffers in arrays
/// and in the batch arena, which a <c>ref struct</c> forbids.
/// </para>
/// <para>
/// <b>Lifetime.</b> The bytes are owned by a <see cref="SegmentOwner"/>. A
/// <see cref="VortexBuffer"/> obtained from one is valid only until that owner's last
/// <see cref="SegmentOwner.Release"/>: the zero-copy contract is that spans stay valid until the
/// batch that handed them out is disposed, and not one instruction longer.
/// </para>
/// <para>
/// <b>Alignment.</b> <see cref="AlignmentExponent"/> is the <em>declared</em> alignment of the
/// segment as the file states it (<c>alignment = 1 &lt;&lt; alignment_exponent</c>, capped at
/// <see cref="VortexLimits.MaxAlignmentExponent"/>).
/// <see cref="IsAligned"/> reports whether the base address actually satisfies it, which
/// <see cref="Slice(int)"/> can legitimately break.
/// </para>
/// </remarks>
internal readonly unsafe struct VortexBuffer
{
    private readonly byte* _pointer;
    private readonly int _length;
    private readonly int _alignmentExponent;

    private VortexBuffer(byte* pointer, int length, int alignmentExponent)
    {
        _pointer = pointer;
        _length = length;
        _alignmentExponent = alignmentExponent;
    }

    /// <summary>The empty buffer: a null base, zero length, declared alignment 1.</summary>
    public static VortexBuffer Empty => default;

    /// <summary>
    /// Wraps <paramref name="length"/> bytes at <paramref name="pointer"/>. Nothing is copied and
    /// nothing is pinned: the caller guarantees the memory outlives every use of the result.
    /// </summary>
    /// <param name="pointer">Base address. May be <see langword="null"/> only when
    /// <paramref name="length"/> is zero.</param>
    /// <param name="length">Length in bytes, non-negative.</param>
    /// <param name="alignmentExponent">Declared alignment exponent, in
    /// <c>[0, <see cref="VortexLimits.MaxAlignmentExponent"/>]</c>.</param>
    /// <exception cref="VortexFormatException">
    /// <paramref name="length"/> is negative, <paramref name="alignmentExponent"/> is out of
    /// range, or <paramref name="pointer"/> is null with a non-zero length.
    /// </exception>
    public static VortexBuffer FromPointer(byte* pointer, int length, int alignmentExponent)
    {
        if (length < 0)
        {
            ThrowNegativeLength(length);
        }

        CheckExponent(alignmentExponent);

        if (pointer is null && length != 0)
        {
            ThrowNullBase(length);
        }

        return new VortexBuffer(pointer, length, alignmentExponent);
    }

    /// <summary>
    /// Wraps an already-pinned span. Pins nothing: the caller guarantees
    /// <paramref name="pinned"/> is fixed (pinned object heap, <c>fixed</c>, native memory, or a
    /// memory-mapped view) for the whole lifetime of the returned buffer.
    /// </summary>
    /// <param name="pinned">The pinned bytes.</param>
    /// <param name="alignmentExponent">Declared alignment exponent, in
    /// <c>[0, <see cref="VortexLimits.MaxAlignmentExponent"/>]</c>.</param>
    /// <exception cref="VortexFormatException">
    /// <paramref name="alignmentExponent"/> is out of range.
    /// </exception>
    public static VortexBuffer FromPinned(ReadOnlySpan<byte> pinned, int alignmentExponent)
    {
        CheckExponent(alignmentExponent);

        if (pinned.IsEmpty)
        {
            // GetReference on an empty span is a null (or one-past-the-end) ref; do not publish it.
            return new VortexBuffer(null, 0, alignmentExponent);
        }

        return new VortexBuffer(
            (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(pinned)),
            pinned.Length,
            alignmentExponent);
    }

    /// <summary>The bytes this buffer views.</summary>
    public ReadOnlySpan<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new ReadOnlySpan<byte>(_pointer, _length);
    }

    /// <summary>Length in bytes.</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    /// <summary><see langword="true"/> when <see cref="Length"/> is zero.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length == 0;
    }

    /// <summary>
    /// Declared alignment in bytes, <c>1 &lt;&lt; <see cref="AlignmentExponent"/></c>. Always at
    /// least 1 and at most <see cref="VortexLimits.MaxAlignment"/>.
    /// </summary>
    public int Alignment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 1 << _alignmentExponent;
    }

    /// <summary>The declared <c>alignment_exponent</c>, in
    /// <c>[0, <see cref="VortexLimits.MaxAlignmentExponent"/>]</c>.</summary>
    public int AlignmentExponent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _alignmentExponent;
    }

    /// <summary>
    /// <see langword="true"/> when the base address really is a multiple of
    /// <see cref="Alignment"/>. A <see cref="Slice(int)"/> at an unaligned offset keeps the
    /// declared exponent but turns this <see langword="false"/>.
    /// </summary>
    public bool IsAligned
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((nuint)_pointer & (nuint)(Alignment - 1)) == 0;
    }

    /// <summary>Whether <paramref name="other"/> views the same bytes: the same base address and the same length.</summary>
    /// <param name="other">The buffer to compare with.</param>
    public bool SameAs(VortexBuffer other) => _pointer == other._pointer && _length == other._length;

    /// <summary>Returns the sub-buffer starting at <paramref name="offset"/>.</summary>
    /// <param name="offset">Byte offset in <c>[0, <see cref="Length"/>]</c>.</param>
    /// <exception cref="VortexFormatException">The offset escapes the buffer.</exception>
    /// <remarks>The declared <see cref="AlignmentExponent"/> is preserved; consult
    /// <see cref="IsAligned"/> for whether the new base still satisfies it.</remarks>
    public VortexBuffer Slice(int offset)
    {
        // Single unsigned compare: catches negative and past-the-end in one branch, and cannot
        // itself overflow the way `offset > _length` on a signed sum can.
        if ((uint)offset > (uint)_length)
        {
            ThrowSlice(offset, 0, _length);
        }

        return new VortexBuffer(_pointer + offset, _length - offset, _alignmentExponent);
    }

    /// <summary>
    /// Returns the sub-buffer <c>[offset, offset + length)</c>.
    /// </summary>
    /// <param name="offset">Byte offset in <c>[0, <see cref="Length"/>]</c>.</param>
    /// <param name="length">Byte length in <c>[0, Length - offset]</c>.</param>
    /// <exception cref="VortexFormatException">The range escapes the buffer.</exception>
    public VortexBuffer Slice(int offset, int length)
    {
        // `(uint)length > (uint)(_length - offset)` is evaluated only once offset is known good,
        // so the subtraction cannot go negative and `offset + length` is never formed at all.
        if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
        {
            ThrowSlice(offset, length, _length);
        }

        return new VortexBuffer(_pointer + offset, length, _alignmentExponent);
    }

    /// <summary>
    /// Reinterprets the bytes as a span of <typeparamref name="T"/> with no copy. This is the
    /// primitive the footer parser uses on <c>SegmentSpec</c> (16 bytes) and <c>Buffer</c>
    /// (8 bytes).
    /// </summary>
    /// <typeparam name="T">An unmanaged element type.</typeparam>
    /// <exception cref="VortexFormatException">
    /// The base address is not a multiple of <c>sizeof(T)</c>, or <see cref="Length"/> is not a
    /// whole multiple of <c>sizeof(T)</c>.
    /// </exception>
    /// <remarks>
    /// Both rejections are deliberate. Truncating a ragged tail would silently drop a segment
    /// spec, and reading unaligned would be a correctness-and-portability hazard we refuse rather
    /// than paper over with a copy — the caller relies on this being zero-copy. Note the
    /// alignment requirement is <c>sizeof(T)</c>, which for a 16-byte struct is stricter than the
    /// natural <c>alignof(T)</c> of 8; use <see cref="TryCast{T}"/> when a fallback is wanted.
    /// </remarks>
    public ReadOnlySpan<T> Cast<T>() where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();

        if ((nuint)_pointer % (nuint)size != 0)
        {
            ThrowCastMisaligned((nuint)_pointer, size);
        }

        if (_length % size != 0)
        {
            ThrowCastRagged(_length, size);
        }

        return new ReadOnlySpan<T>(_pointer, _length / size);
    }

    /// <summary>
    /// Non-throwing <see cref="Cast{T}"/>.
    /// </summary>
    /// <typeparam name="T">An unmanaged element type.</typeparam>
    /// <param name="values">The reinterpreted span, or the empty span when the cast is refused.</param>
    /// <returns><see langword="false"/> when the base is misaligned for
    /// <typeparamref name="T"/> or the length is ragged.</returns>
    public bool TryCast<T>(out ReadOnlySpan<T> values) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();

        if ((nuint)_pointer % (nuint)size != 0 || _length % size != 0)
        {
            values = default;
            return false;
        }

        values = new ReadOnlySpan<T>(_pointer, _length / size);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckExponent(int alignmentExponent)
    {
        // alignment_exponent is a u8 on the wire, so an unchecked file can demand 2^255; the cap
        // is 6, that is 64 bytes.
        if ((uint)alignmentExponent > (uint)VortexLimits.MaxAlignmentExponent)
        {
            ThrowExponent(alignmentExponent);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowExponent(int alignmentExponent) =>
        throw new VortexFormatException(
            $"alignment_exponent {alignmentExponent} is outside [0, " +
            $"{VortexLimits.MaxAlignmentExponent}].");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNegativeLength(int length) =>
        throw new VortexFormatException($"Buffer length {length} is negative.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNullBase(int length) =>
        throw new VortexFormatException(
            $"A null base address cannot back a buffer of {length} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowSlice(int offset, int length, int available) =>
        throw new VortexFormatException(
            $"Buffer slice [{offset}, {(long)offset + length}) is outside the available " +
            $"{available} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowCastMisaligned(nuint address, int size) =>
        throw new VortexFormatException(
            $"Cannot reinterpret the buffer as {size}-byte elements: the base address 0x" +
            $"{address:x} is not a multiple of {size}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowCastRagged(int length, int size) =>
        throw new VortexFormatException(
            $"Cannot reinterpret {length} bytes as {size}-byte elements: {length % size} " +
            "trailing bytes would be dropped.");
}
