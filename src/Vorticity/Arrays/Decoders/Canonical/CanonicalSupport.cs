// Shared, allocation-free primitives every canonical decoder needs: the bounds and shape checks
// that are class I (docs/08-semantics.md §5), the bit plumbing `vortex.bool`'s bit offset forces on
// everything downstream, and the 16-byte Arrow view layout that `vortex.varbin` and
// `vortex.varbinview` both produce.
//
// vortex-array-0.86.1/src/arrays/varbinview/view.rs fixes the view layout; the rest is transcribed
// from the per-encoding `deserialize` bodies cited in each decoder.
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Validation and bit/byte plumbing shared by the fourteen canonical decoders. Internal by
/// design: nothing outside this directory should need it, and everything in it throws
/// <see cref="VortexFormatException"/> and nothing else.
/// </summary>
internal static class CanonicalSupport
{
    /// <summary>Bytes in one Arrow <c>BinaryView</c>.</summary>
    internal const int ViewSize = 16;

    /// <summary>Longest value a view can carry inline. <c>BinaryView::MAX_INLINED_SIZE</c>.</summary>
    internal const int MaxInlineViewLength = 12;

    /// <summary>
    /// The strictest alignment we ever demand of a file-supplied buffer. Upstream asks for
    /// <c>align_of</c> of the element type, which never exceeds 16 even for <c>i256</c>; demanding
    /// the full 32 bytes there would reject files whose segment alignment exponent is 4, which is
    /// the largest any observed writer emits (Phase 1 contract §0a C4).
    /// </summary>
    internal const int MaxRequiredAlignment = 16;

    /// <summary>Allocate without the zero-fill, for a decoder that writes every byte.</summary>
    /// <param name="context">The decode context, for the size ceiling.</param>
    /// <param name="byteLength">Size in bytes.</param>
    /// <param name="alignment">A power of two.</param>
    /// <param name="destination">The writable block, NOT zeroed.</param>
    /// <returns>A non-owning view over the same bytes.</returns>
    /// <remarks>
    /// Only for a decoder that provably writes every byte; see
    /// <see cref="CanonicalArena.AllocateUninitialized"/> for why that bar is where it is.
    /// </remarks>
    internal static VortexBuffer AllocateUninitialized(
        ArrayDecodeContext context, int byteLength, int alignment, out Span<byte> destination)
    {
        RequireWithinBudget(context, byteLength);
        return context.Canonical.AllocateUninitialized(byteLength, alignment, out destination);
    }

    /// <summary>
    /// Materializes a buffer, refusing one larger than
    /// <see cref="Vorticity.File.VortexReadOptions.MaxDecompressedSize"/>.
    /// </summary>
    /// <remarks>
    /// Phase 1 contract §1.5: "no allocation sized directly by a file-supplied value without a cap
    /// first". Several of these sizes are products of a file-supplied row count and a
    /// schema-supplied width - a <c>vortex.constant</c> under
    /// <c>fixed_size_list(i32)[100000]</c> asks for 3 GB from a 40-byte node - and
    /// <see cref="ArrayDecodeContext.CheckedMultiply"/> only stops the ones that overflow
    /// <see cref="int"/>. The ceiling is the caller's, not a new constant of ours.
    /// </remarks>
    internal static VortexBuffer Allocate(
        ArrayDecodeContext context, int byteLength, int alignment, out Span<byte> destination)
    {
        RequireWithinBudget(context, byteLength);
        return context.Canonical.Allocate(byteLength, alignment, out destination);
    }

    /// <summary><see cref="Allocate(ArrayDecodeContext, int, int, out Span{byte})"/> without the span.</summary>
    internal static VortexBuffer Allocate(ArrayDecodeContext context, int byteLength, int alignment)
    {
        RequireWithinBudget(context, byteLength);
        return context.Canonical.Allocate(byteLength, alignment);
    }

    private static void RequireWithinBudget(ArrayDecodeContext context, int byteLength)
    {
        if (byteLength > context.Options.MaxDecompressedSize)
        {
            ThrowOverBudget(byteLength, context.Options.MaxDecompressedSize);
        }
    }

    /// <summary>Requires <paramref name="dtype"/> to be of <paramref name="kind"/>.</summary>
    internal static void RequireKind(DType dtype, DTypeKind kind, string encodingId)
    {
        if (dtype.IsDefault || dtype.Kind != kind)
        {
            ThrowKind(dtype, kind.ToString(), encodingId);
        }
    }

    /// <summary>Requires <paramref name="dtype"/> to be <c>Utf8</c> or <c>Binary</c>.</summary>
    internal static void RequireBinaryLike(DType dtype, string encodingId)
    {
        if (dtype.IsDefault || dtype.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary))
        {
            ThrowKind(dtype, "Utf8 or Binary", encodingId);
        }
    }

    /// <summary>
    /// Requires <paramref name="buffer"/> to start on a multiple of <paramref name="alignment"/>.
    /// Class I for every reinterpreting read: the caller relies on the decode being zero-copy
    /// (docs/03-architecture.md §4 invariant 2), so a misaligned buffer is rejected rather than
    /// silently copied.
    /// </summary>
    internal static unsafe void RequireAligned(VortexBuffer buffer, int alignment, string what)
    {
        if (alignment <= 1 || buffer.Length == 0)
        {
            return;
        }

        int effective = Math.Min(alignment, MaxRequiredAlignment);
        nuint address = (nuint)Unsafe.AsPointer(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(buffer.Span)));
        if ((address & (nuint)(effective - 1)) != 0)
        {
            ThrowMisaligned(what, effective);
        }
    }

    /// <summary>
    /// Requires <paramref name="buffer"/> to hold exactly <paramref name="count"/> elements of
    /// <paramref name="width"/> bytes, and to be aligned for them.
    /// </summary>
    internal static void RequireExactBuffer(VortexBuffer buffer, int count, int width, string what)
    {
        int expected = ArrayDecodeContext.CheckedMultiply(count, width, what);
        if (buffer.Length != expected)
        {
            ThrowBufferLength(what, buffer.Length, expected);
        }

        RequireAligned(buffer, width, what);
    }

    /// <summary>Requires an integer (not floating-point) physical type.</summary>
    internal static void RequireIntegerPType(PType ptype, string what)
    {
        if (!PTypeExtensions.IsDefined(ptype) || !ptype.IsInteger())
        {
            ThrowNotInteger(what, ptype);
        }
    }

    /// <summary>
    /// Requires the canonical node at <paramref name="index"/> to be a Primitive of
    /// <paramref name="ptype"/> and <paramref name="length"/> rows. A child's own encoding never
    /// gets to decide what it means (Phase 1 contract §2.5).
    /// </summary>
    internal static CanonicalNode RequirePrimitiveChild(
        ArrayDecodeContext context, int index, PType ptype, int length, string what)
    {
        CanonicalNode node = context.Canonical.GetNode(index);
        if (node.Kind != CanonicalKind.Primitive || node.PType != ptype || node.Length != length)
        {
            ThrowChildShape(what, ptype, length);
        }

        return node;
    }

    /// <summary>
    /// Reads element <paramref name="index"/> of an integer buffer as a <see cref="long"/>,
    /// saturating rather than throwing: a <c>u64</c> above <see cref="long.MaxValue"/> saturates
    /// and is then rejected by the range check every caller applies afterwards.
    /// </summary>
    internal static long ReadInteger(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U64 => Saturate(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8))),
        PType.I8 => (sbyte)bytes[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
        _ => ThrowNotInteger<long>("offset", ptype),
    };

    /// <summary>Writes <paramref name="value"/> as element <paramref name="index"/>, truncating.</summary>
    internal static void WriteInteger(Span<byte> bytes, PType ptype, int index, long value)
    {
        switch (ptype)
        {
            case PType.U8:
            case PType.I8:
                bytes[index] = unchecked((byte)value);
                break;
            case PType.U16:
            case PType.I16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(index * 2, 2), unchecked((ushort)value));
                break;
            case PType.U32:
            case PType.I32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(index * 4, 4), unchecked((uint)value));
                break;
            case PType.U64:
            case PType.I64:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(index * 8, 8), unchecked((ulong)value));
                break;
            default:
                ThrowNotInteger("offset", ptype);
                break;
        }
    }

    /// <summary>Reads bit <paramref name="index"/> of an LSB-first bitmap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool BitAt(ReadOnlySpan<byte> bits, int index) =>
        (bits[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets bit <paramref name="index"/> of an LSB-first bitmap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetBit(Span<byte> bits, int index) =>
        bits[index >> 3] |= (byte)(1 << (index & 7));

    /// <summary>
    /// Copies <paramref name="count"/> bits from <paramref name="source"/> at bit
    /// <paramref name="sourceBit"/> to <paramref name="destination"/> at bit
    /// <paramref name="destinationBit"/>. Scalar and simple on purpose: SIMD is Phase 2 and
    /// invariant 4 requires the scalar path to exist and be tested regardless.
    /// </summary>
    internal static void CopyBits(
        ReadOnlySpan<byte> source, int sourceBit, Span<byte> destination, int destinationBit, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (BitAt(source, sourceBit + i))
            {
                SetBit(destination, destinationBit + i);
            }
        }
    }

    /// <summary>Sets <paramref name="count"/> bits starting at <paramref name="destinationBit"/>.</summary>
    internal static void SetBits(Span<byte> destination, int destinationBit, int count)
    {
        for (int i = 0; i < count; i++)
        {
            SetBit(destination, destinationBit + i);
        }
    }

    /// <summary>Bytes needed to hold <paramref name="bitCount"/> bits.</summary>
    internal static int BitmapByteCount(int bitCount) => (int)(((long)bitCount + 7) / 8);

    /// <summary>
    /// Writes an Arrow view for a value that is stored out of line.
    /// <c>{u32 size, [4]u8 prefix, u32 buffer_index, u32 offset}</c>, little-endian throughout
    /// (vortex-array-0.86.1/src/arrays/varbinview/view.rs <c>Ref</c>).
    /// </summary>
    internal static void WriteReferenceView(
        Span<byte> view, int size, ReadOnlySpan<byte> value, int bufferIndex, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)size);
        value[..4].CopyTo(view[4..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], (uint)bufferIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
    }

    /// <summary>
    /// Writes an Arrow view for a value of 12 bytes or fewer, held inside the view itself.
    /// <c>{u32 size, [12]u8 data}</c>.
    /// </summary>
    internal static void WriteInlineView(Span<byte> view, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
        value.CopyTo(view[4..]);
    }

    /// <summary>
    /// Writes one complete 16-byte Arrow view - inline or by reference - as two register stores.
    /// </summary>
    /// <param name="view">Exactly 16 writable bytes.</param>
    /// <param name="value">The value's bytes, exactly <paramref name="size"/> of them.</param>
    /// <param name="size">The value's length.</param>
    /// <param name="bufferIndex">Data buffer the value lives in, when it is not inline.</param>
    /// <param name="offset">Byte offset within that buffer, when it is not inline.</param>
    /// <returns><see langword="true"/> when the view references a data buffer.</returns>
    /// <remarks>
    /// <para>
    /// THE SLOW PART OF BUILDING VIEWS WAS THE CALL, NOT THE COPY. The inline case was
    /// <c>view.Clear()</c> followed by <c>value.CopyTo(view[4..])</c>: a memset and a
    /// <c>SpanHelpers.Memmove</c> PER ROW, both out-of-line because the length is a variable, to
    /// move at most twelve bytes. On a 1M-row <c>vortex.parquet.variant</c> scan - two varbin
    /// columns and nothing else - <c>Memmove</c> was <b>78% of the whole scan</b>.
    /// </para>
    /// <para>
    /// A view is 16 bytes and every field of it is known here, so it is composed in two
    /// <see cref="ulong"/>s and stored. The inline value is gathered with the overlapping-read
    /// ladder a small <c>memcpy</c> uses internally, which is branchy but never leaves a register
    /// and never reads outside <paramref name="value"/>: at most <paramref name="size"/> bytes are
    /// read, from a span that is exactly that long.
    /// </para>
    /// <para>
    /// Little-endian is a module-initializer invariant (docs/09-contracts.md §7), so the words are
    /// composed by shifting rather than by <see cref="BinaryPrimitives"/>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool WriteView(
        Span<byte> view, ReadOnlySpan<byte> value, int size, int bufferIndex, int offset) =>
        WriteView(
            ref MemoryMarshal.GetReference(view),
            ref MemoryMarshal.GetReference(value),
            size,
            bufferIndex,
            offset);

    /// <summary>
    /// <see cref="WriteView(Span{byte}, ReadOnlySpan{byte}, int, int, int)"/> addressed by
    /// reference, for a loop that has already established both ranges.
    /// </summary>
    /// <param name="destination">The first of sixteen writable bytes.</param>
    /// <param name="source">The first of <paramref name="size"/> readable value bytes.</param>
    /// <param name="size">The value's length.</param>
    /// <param name="bufferIndex">Data buffer the value lives in, when it is not inline.</param>
    /// <param name="offset">Byte offset within that buffer, when it is not inline.</param>
    /// <returns><see langword="true"/> when the view references a data buffer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool WriteView(
        ref byte destination, ref byte source, int size, int bufferIndex, int offset)
    {
        ulong low;
        ulong high;
        if (size > MaxInlineViewLength)
        {
            // {u32 size, u32 prefix, u32 buffer, u32 offset}
            low = (uint)size | ((ulong)Unsafe.ReadUnaligned<uint>(ref source) << 32);
            high = (uint)bufferIndex | ((ulong)(uint)offset << 32);
        }
        else
        {
            // {u32 size, [12]u8 value}, zero-padded past the value.
            Gather(ref source, size, out ulong first, out uint rest);
            low = (uint)size | ((first & 0xFFFF_FFFFUL) << 32);
            high = (first >> 32) | ((ulong)rest << 32);
        }

        Unsafe.WriteUnaligned(ref destination, low);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, sizeof(ulong)), high);
        return size > MaxInlineViewLength;
    }

    /// <summary>
    /// Reads <paramref name="size"/> bytes (0..12) into <paramref name="first"/> - the low eight -
    /// and <paramref name="rest"/> - the remaining up-to-four - zero-padded.
    /// </summary>
    /// <remarks>
    /// Overlapping reads rather than a loop: the 4..8 and 8..12 cases read one word at the value's
    /// START and one ending at its END, which together cover every byte exactly and read none
    /// beyond it. Only the 1..3 case is byte-wise, where a word read would overrun.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Gather(ref byte source, int size, out ulong first, out uint rest)
    {
        if (size >= sizeof(ulong))
        {
            first = Unsafe.ReadUnaligned<ulong>(ref source);
            int tail = size - sizeof(ulong);
            rest = tail == 0
                ? 0u
                : Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, size - sizeof(uint)))
                    >> ((sizeof(uint) - tail) * 8);
            return;
        }

        rest = 0;
        if (size >= sizeof(uint))
        {
            ulong head = Unsafe.ReadUnaligned<uint>(ref source);
            ulong end = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, size - sizeof(uint)));
            first = head | (end << ((size - sizeof(uint)) * 8));
            return;
        }

        first = 0;
        if (size == 0)
        {
            return;
        }

        first = source;
        if (size > 1)
        {
            first |= (ulong)Unsafe.Add(ref source, 1) << 8;
        }

        if (size > 2)
        {
            first |= (ulong)Unsafe.Add(ref source, 2) << 16;
        }
    }

    private static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowOverBudget(int byteLength, long max) =>
        throw new VortexFormatException(
            $"Decoding this array would materialize {byteLength} bytes in one buffer; " +
            $"VortexReadOptions.MaxDecompressedSize is {max}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowKind(DType dtype, string expected, string encodingId) =>
        throw new VortexFormatException(
            $"{encodingId} produces a {expected} dtype; it was asked for " +
            $"{(dtype.IsDefault ? "<none>" : dtype.Kind.ToString())}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowMisaligned(string what, int alignment) =>
        throw new VortexFormatException(
            $"{what} is not aligned to {alignment} bytes; a misaligned buffer cannot be read " +
            "zero-copy and is rejected rather than silently copied.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowBufferLength(string what, int actual, int expected) =>
        throw new VortexFormatException(
            $"{what} holds {actual} bytes; exactly {expected} are required.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNotInteger(string what, PType ptype) =>
        throw new VortexFormatException(
            $"{what} must be an integer physical type; the file says {(byte)ptype}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static T ThrowNotInteger<T>(string what, PType ptype) =>
        throw new VortexFormatException(
            $"{what} must be an integer physical type; the file says {(byte)ptype}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowChildShape(string what, PType ptype, int length) =>
        throw new VortexFormatException(
            $"{what} must decode to a {ptype.Name()} primitive of {length} rows.");
}
