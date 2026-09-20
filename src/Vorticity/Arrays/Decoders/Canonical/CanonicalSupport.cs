using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Validation and bit/byte plumbing shared by the canonical decoders: the bounds and shape checks
/// a file-supplied node needs, the bit addressing a sub-byte bitmap offset forces on everything
/// downstream, and the sixteen-byte view layout the variable-width encodings produce. Internal by
/// design: nothing outside this directory should need it, and everything in it throws
/// <see cref="VortexFormatException"/> and nothing else.
/// </summary>
internal static class CanonicalSupport
{
    /// <summary>Bytes in one Arrow <c>BinaryView</c>.</summary>
    internal const int ViewSize = 16;

    /// <summary>Longest value a view can carry inside itself, instead of in a data buffer.</summary>
    internal const int MaxInlineViewLength = 12;

    /// <summary>
    /// The strictest alignment ever demanded of a file-supplied buffer. No element type needs more
    /// than this, and demanding more would reject files from writers that align their segments to
    /// sixteen bytes.
    /// </summary>
    internal const int MaxRequiredAlignment = 16;

    /// <summary>Allocate without the zero-fill, for a decoder that writes every byte.</summary>
    /// <param name="context">The decode context, for the size ceiling.</param>
    /// <param name="byteLength">Size in bytes.</param>
    /// <param name="alignment">A power of two.</param>
    /// <param name="destination">The writable block, left as the pool found it.</param>
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
    /// No allocation may be sized directly by a file-supplied value without a cap first. Several of
    /// these sizes are products of a file-supplied row count and a schema-supplied width, so a tiny
    /// node can ask for gigabytes, and
    /// <see cref="ArrayDecodeContext.CheckedMultiply"/> only stops the ones that overflow
    /// <see cref="int"/>. The ceiling is the caller's, not a constant of this library's.
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

    /// <summary>
    /// Charges <paramref name="byteLength"/> against the read's decompression ceiling without
    /// allocating it, for a node that stands for more bytes than it stores.
    /// </summary>
    /// <param name="context">The decode context carrying the ceiling.</param>
    /// <param name="byteLength">The size the node stands for.</param>
    /// <remarks>
    /// The constant form keeps one element and a row count, so nothing is allocated at decode and
    /// the guard the two <c>Allocate</c> overloads apply never fires. It still has to fire: the row
    /// count is file-supplied, <c>MaterializeConstant</c> expands it in full the moment a caller
    /// asks for a span, and the arena does not check, its contract being that the size arrives
    /// already validated. Charging the ceiling here, where the row count is first seen, also keeps
    /// the constant and the materialized forms indistinguishable to a caller: a file refused in one
    /// and accepted in the other would make the choice between them observable.
    /// </remarks>
    /// <exception cref="VortexFormatException">It exceeds the ceiling.</exception>
    internal static void RequireStandsForWithinBudget(ArrayDecodeContext context, int byteLength) =>
        RequireWithinBudget(context, byteLength);

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
    /// Requires <paramref name="buffer"/> to start on a multiple of <paramref name="alignment"/>,
    /// which every reinterpreting read needs. Callers rely on the decode staying zero-copy, so a
    /// misaligned buffer is rejected rather than silently copied.
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
    /// gets to decide what it means.
    /// </summary>
    /// <remarks>
    /// A constant child is expanded here, and this is the one place that does it: every caller is
    /// about to read a contiguous span of <paramref name="length"/> values -- offsets to walk,
    /// lengths to sum, patch indices to binary-search -- and a constant node has an element, not a
    /// span. Expanding restores exactly the bytes a dense child would have carried, at the cost a
    /// dense child would have had, so nothing is given up by it.
    /// <para>
    /// The constant form is there to save a consumer from reading many copies of one value, not to
    /// save a decoder from a side table it has to walk; that is why the expansion belongs on the
    /// decoder's side of the boundary.
    /// </para>
    /// </remarks>
    internal static CanonicalNode RequirePrimitiveChild(
        ArrayDecodeContext context, int index, PType ptype, int length, string what)
    {
        CanonicalNode node = context.Canonical.GetNode(ExpandIfConstant(context, index));
        if (node.Kind != CanonicalKind.Primitive || node.PType != ptype || node.Length != length)
        {
            ThrowChildShape(what, ptype, length);
        }

        return node;
    }

    /// <summary>
    /// The node at <paramref name="index"/>, or a dense Primitive equal to it when it is a
    /// primitive <see cref="CanonicalKind.Constant"/>.
    /// </summary>
    /// <returns>
    /// <paramref name="index"/> itself for anything else, so a call site can use this
    /// unconditionally.
    /// </returns>
    /// <remarks>
    /// See <see cref="RequirePrimitiveChild"/> for why expanding here gives nothing up. This is the
    /// form for the sites that check the child's shape themselves rather than through that helper,
    /// because their messages name a patch count rather than the array's row count.
    /// </remarks>
    internal static int ExpandIfConstant(ArrayDecodeContext context, int index)
    {
        CanonicalNode node = context.Canonical.GetNode(index);
        if (node.Kind != CanonicalKind.Constant || node.DType.Kind != DTypeKind.Primitive)
        {
            return index;
        }

        PType ptype = node.DType.PType;
        int length = node.Length;
        int width = ptype.ByteWidth();
        int bytes = ArrayDecodeContext.CheckedMultiply(length, width, "expanded constant");

        // Uninitialized: `Tile` writes every byte from the element, and the element is the node's
        // own `ConstantElement`, which `AddConstant` refused to leave empty.
        VortexBuffer values = AllocateUninitialized(context, bytes, width, out Span<byte> writable);
        if (length != 0)
        {
            node.ConstantElement[..width].CopyTo(writable);
            RowKernels.Tile(writable, writable[..width]);
        }

        return context.Canonical.AddPrimitive(node.DType, length, node.Validity, ptype, values);
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
    /// <paramref name="destinationBit"/>.
    /// </summary>
    internal static void CopyBits(
        ReadOnlySpan<byte> source, int sourceBit, Span<byte> destination, int destinationBit, int count) =>
        BitmapKernels.CopyRange(source, sourceBit, destination, destinationBit, count);

    /// <summary>Sets <paramref name="count"/> bits starting at <paramref name="destinationBit"/>.</summary>
    internal static void SetBits(Span<byte> destination, int destinationBit, int count) =>
        BitmapKernels.SetRange(destination, destinationBit, count);

    /// <summary>Bytes needed to hold <paramref name="bitCount"/> bits.</summary>
    internal static int BitmapByteCount(int bitCount) => (int)(((long)bitCount + 7) / 8);

    /// <summary>
    /// Writes an Arrow view for a value that is stored out of line.
    /// <c>{u32 size, [4]u8 prefix, u32 buffer_index, u32 offset}</c>, little-endian throughout.
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
    /// The expensive part of building views is the call, not the copy: writing an inline view with
    /// a clear followed by a span copy costs two out-of-line calls per row, because the length is a
    /// variable, to move at most twelve bytes. On a scan of nothing but variable-width columns that
    /// dominates everything else.
    /// </para>
    /// <para>
    /// A view is 16 bytes and every field of it is known here, so it is composed in two
    /// <see cref="ulong"/>s and stored. The inline value is gathered with the overlapping-read
    /// ladder a small <c>memcpy</c> uses internally, which is branchy but never leaves a register
    /// and never reads outside <paramref name="value"/>: at most <paramref name="size"/> bytes are
    /// read, from a span that is exactly that long.
    /// </para>
    /// <para>
    /// A little-endian host is checked once at start-up, so the words are composed by shifting
    /// rather than through <see cref="BinaryPrimitives"/>.
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
    /// start and one ending at its last byte, which together cover every byte and read none beyond
    /// it. Only the 1..3 case is byte-wise, where a word read would overrun.
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
