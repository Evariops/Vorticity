// Pass 2 of two: write the bytes.
//
// Every encoder writes row `i` at `offsets[i] + cursors[i]` and advances `cursors[i]` by exactly
// the contribution RowSizeKernel computed for the same column. That invariant is what lets the
// cursor array BE the output's `sizes` array once the last column is done, and what makes a
// mismatch between the two passes show up as a corrupt row rather than as a silent overlap: the
// caller's destination is sized from pass 1, so a column that writes more than it measured runs
// off the end and throws.
//
// The encoders are column-at-a-time, not row-at-a-time: one pass per column over all rows. The
// branch on validity and the branch on type are then hoisted out of the inner loop, which a
// row-major encoder cannot do.
//
// Transcribed from `field_encode` and its `encode_*` helpers in vortex-row/src/codec.rs at 0.86.1.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.RowEncoding;

/// <summary>The write pass.</summary>
internal static class RowEncodeKernel
{
    /// <summary>Encodes one column into the per-row slots named by the offsets and cursors.</summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">The column's canonical node index.</param>
    /// <param name="field">The column's sort options, inherited unchanged by every child.</param>
    /// <param name="offsets">Where each row starts in <paramref name="destination"/>.</param>
    /// <param name="cursors">How far each row is already written; advanced by this call.</param>
    /// <param name="destination">The output buffer.</param>
    /// <exception cref="VortexUnsupportedException">The column's dtype has no defined ordering.</exception>
    /// <exception cref="VortexFormatException">The column's data contradicts its dtype.</exception>
    internal static void Encode(
        CanonicalArena arena,
        int nodeIndex,
        RowSortField field,
        ReadOnlySpan<int> offsets,
        Span<int> cursors,
        Span<byte> destination)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Length > offsets.Length || node.Length > cursors.Length)
        {
            throw new VortexFormatException(
                $"A {node.Length}-row column was given {Math.Min(offsets.Length, cursors.Length)} row slots.");
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                EncodeNull(node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.Bool:
                EncodeBool(arena, node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.Primitive:
                EncodePrimitive(arena, node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.Decimal:
                EncodeDecimal(arena, node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.VarBinView:
                EncodeVarBin(arena, node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.Struct:
                EncodeStruct(arena, node, field, offsets, cursors, destination);
                return;

            case CanonicalKind.FixedSizeList:
                EncodeFixedSizeList(arena, node, field, offsets, cursors, destination);
                return;

            default:
                throw RowThrow.UnsupportedCanonical(node);
        }
    }

    // ------------------------------------------------------------------------------------ leaves

    /// <summary>The Null dtype: a sentinel and no body, because there is no value to order by.</summary>
    private static void EncodeNull(
        CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        byte sentinel = RowSentinels.FixedNull(field);
        for (int i = 0; i < node.Length; i++)
        {
            destination[offsets[i] + cursors[i]] = sentinel;
            cursors[i]++;
        }
    }

    /// <summary>
    /// Bool: <c>0x01</c> for false and <c>0x02</c> for true, so false sorts first, complemented
    /// when descending. Not <c>0x00</c>/<c>0x01</c>: a null's zero-filled body must stay
    /// distinguishable from a false.
    /// </summary>
    private static void EncodeBool(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int bitOffset = node.BitOffset;
        byte nullByte = RowSentinels.FixedNull(field);
        byte xor = field.Descending ? (byte)0xFF : (byte)0x00;

        for (int i = 0; i < node.Length; i++)
        {
            int pos = offsets[i] + cursors[i];
            if (validity.IsValid(i))
            {
                destination[pos] = RowSentinels.FixedNonNull;
                destination[pos + 1] = (byte)((GetBit(bits, bitOffset + i) ? 0x02 : 0x01) ^ xor);
            }
            else
            {
                destination[pos] = nullByte;
                destination[pos + 1] = 0;
            }

            cursors[i] += 2;
        }
    }

    private static void EncodePrimitive(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        VortexBuffer values = node.Values;
        switch (node.PType)
        {
            case PType.U8:
                EncodeFixed<byte, OrderU8>(values.Span, validity, field, offsets, cursors, destination);
                return;
            case PType.U16:
                EncodeFixed<ushort, OrderU16>(values.Cast<ushort>(), validity, field, offsets, cursors, destination);
                return;
            case PType.U32:
                EncodeFixed<uint, OrderU32>(values.Cast<uint>(), validity, field, offsets, cursors, destination);
                return;
            case PType.U64:
                EncodeFixed<ulong, OrderU64>(values.Cast<ulong>(), validity, field, offsets, cursors, destination);
                return;
            case PType.I8:
                EncodeFixed<byte, OrderI8>(values.Span, validity, field, offsets, cursors, destination);
                return;
            case PType.I16:
                EncodeFixed<ushort, OrderI16>(values.Cast<ushort>(), validity, field, offsets, cursors, destination);
                return;
            case PType.I32:
                EncodeFixed<uint, OrderI32>(values.Cast<uint>(), validity, field, offsets, cursors, destination);
                return;
            case PType.I64:
                EncodeFixed<ulong, OrderI64>(values.Cast<ulong>(), validity, field, offsets, cursors, destination);
                return;
            case PType.F16:
                EncodeFixed<ushort, OrderF16>(values.Cast<ushort>(), validity, field, offsets, cursors, destination);
                return;
            case PType.F32:
                EncodeFixed<uint, OrderF32>(values.Cast<uint>(), validity, field, offsets, cursors, destination);
                return;
            default:
                EncodeFixed<ulong, OrderF64>(values.Cast<ulong>(), validity, field, offsets, cursors, destination);
                return;
        }
    }

    /// <summary>
    /// The one loop every fixed-width type goes through: sentinel, ordered value big-endian,
    /// complemented when descending.
    /// </summary>
    /// <remarks>
    /// A null writes its sentinel and ZERO-FILLS the value bytes - zero even under
    /// <c>descending</c>, because the fill is not a value and inverting it would make two nulls
    /// of the same column compare unequal to a null written by another implementation.
    /// </remarks>
    private static void EncodeFixed<T, TOrder>(
        ReadOnlySpan<T> values, RowValidity validity, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>
        where TOrder : struct, IRowOrdering<T>
    {
        int stride = Unsafe.SizeOf<T>() + 1;
        bool descending = field.Descending;

        if (validity.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                int pos = offsets[i] + cursors[i];
                Span<byte> slot = destination.Slice(pos, stride);
                slot[0] = RowSentinels.FixedNonNull;
                Write(TOrder.ToOrdered(values[i]), descending, slot.Slice(1));
                cursors[i] += stride;
            }

            return;
        }

        byte nullByte = RowSentinels.FixedNull(field);
        for (int i = 0; i < values.Length; i++)
        {
            int pos = offsets[i] + cursors[i];
            Span<byte> slot = destination.Slice(pos, stride);
            if (validity.IsValid(i))
            {
                slot[0] = RowSentinels.FixedNonNull;
                Write(TOrder.ToOrdered(values[i]), descending, slot.Slice(1));
            }
            else
            {
                slot[0] = nullByte;
                slot.Slice(1).Clear();
            }

            cursors[i] += stride;
        }
    }

    /// <summary>
    /// Decimal: the signed-integer encoding of the unscaled value, at the width the DECLARED
    /// precision implies.
    /// </summary>
    /// <remarks>
    /// The key width comes from the precision and never from this chunk's physical storage width,
    /// because one logical column's chunks can compress to different physical widths and keys
    /// taken from the physical width would then not be comparable across them.
    ///
    /// A null slot's backing bytes are unspecified and may not fit the key width at all, so the
    /// fit check applies to valid rows only - checking it everywhere would reject files the
    /// reference accepts.
    /// </remarks>
    private static void EncodeDecimal(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        if (node.Storage == DecimalStorageType.I256)
        {
            throw new VortexUnsupportedException(
                "decimal256", VortexComponentKind.DType, "Row encoding is not defined for 256-bit decimals.");
        }

        RowValidity validity = RowValidity.Resolve(arena, node);
        int keyWidth = DecimalStorage.ByteWidth(RowWidths.KeyStorage(node.Precision));
        int physicalWidth = DecimalStorage.ByteWidth(node.Storage);
        ReadOnlySpan<byte> storage = node.Values.Span;
        byte nullByte = RowSentinels.FixedNull(field);
        bool descending = field.Descending;
        int stride = keyWidth + 1;
        Span<byte> wide = stackalloc byte[16];

        for (int i = 0; i < node.Length; i++)
        {
            int pos = offsets[i] + cursors[i];
            Span<byte> slot = destination.Slice(pos, stride);
            if (validity.IsValid(i))
            {
                Int128 value = ReadStorage(storage, physicalWidth, i);
                if (!FitsKey(value, keyWidth))
                {
                    throw RowThrow.DecimalDoesNotFit(i, node.Precision, keyWidth);
                }

                // The sign bit to flip is the key width's, not Int128's: the low `keyWidth` bytes
                // of the two's complement value are the key, and their top bit is the sign.
                UInt128 ordered = unchecked((UInt128)value) ^ (UInt128.One << ((keyWidth * 8) - 1));
                if (descending)
                {
                    ordered = ~ordered;
                }

                slot[0] = RowSentinels.FixedNonNull;

                // UInt128 implements WriteBigEndian explicitly, so the generic path above cannot
                // reach it on a concrete UInt128; BinaryPrimitives is the same BSWAP pair.
                BinaryPrimitives.WriteUInt128BigEndian(wide, ordered);
                wide.Slice(16 - keyWidth, keyWidth).CopyTo(slot.Slice(1));
            }
            else
            {
                slot[0] = nullByte;
                slot.Slice(1).Clear();
            }

            cursors[i] += stride;
        }
    }

    private static void EncodeVarBin(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        ReadOnlySpan<byte> views = node.Views.Span;
        byte nullByte = RowSentinels.VarNull(field);
        byte emptyByte = RowSentinels.VarEmpty(field);
        byte nonEmptyByte = RowSentinels.VarNonEmpty(field);
        bool descending = field.Descending;

        for (int i = 0; i < node.Length; i++)
        {
            int pos = offsets[i] + cursors[i];
            if (!validity.IsValid(i))
            {
                destination[pos] = nullByte;
                cursors[i] += RowWidths.VarNullSize;
                continue;
            }

            ReadOnlySpan<byte> view = RowBytes.View(views, i);
            int length = RowBytes.ViewLength(view);
            if (length == 0)
            {
                // Three sentinels, not two: byte 0 alone must separate null from empty from
                // non-empty, or a following column's bytes line up against another row's padding
                // and the multi-column order breaks.
                destination[pos] = emptyByte;
                cursors[i] += RowWidths.VarEmptySize;
                continue;
            }

            ReadOnlySpan<byte> bytes = ResolveView(node, view, length, i);
            destination[pos] = nonEmptyByte;
            int written = RowBytes.WriteVarBody(bytes, destination.Slice(pos + 1), descending);
            cursors[i] += 1 + written;
        }
    }

    // --------------------------------------------------------------------------------- composites

    private static void EncodeStruct(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        int rows = node.Length;
        byte nullByte = RowSentinels.FixedNull(field);

        for (int i = 0; i < rows; i++)
        {
            destination[offsets[i] + cursors[i]] =
                validity.IsValid(i) ? RowSentinels.FixedNonNull : nullByte;
            cursors[i]++;
        }

        for (int f = 0; f < node.FieldCount; f++)
        {
            int childIndex = node.GetFieldIndex(f);
            CanonicalNode child = arena.GetNode(childIndex);
            RequireLength(child.Length, rows, "struct field");
            DType childType = child.DType;
            RowWidth width = RowWidths.For(childType);
            if (width.IsFixed)
            {
                // Encode every row, then overwrite the null parents. Writing the child first and
                // correcting after is not laziness: it keeps the cursor arithmetic in one place,
                // and the correction is what CANONICALIZES the null body - two null parents must
                // encode byte-equal no matter what their child arrays happen to hold underneath.
                Encode(arena, childIndex, field, offsets, cursors, destination);
                byte childNull = RowSentinels.ChildCanonicalNull(childType, field);
                for (int i = 0; i < rows; i++)
                {
                    if (!validity.IsValid(i))
                    {
                        int end = offsets[i] + cursors[i];
                        int start = end - width.Width;
                        destination[start] = childNull;
                        destination.Slice(start + 1, end - start - 1).Clear();
                    }
                }

                continue;
            }

            EncodeVariableChild(arena, childIndex, childType, field, validity, rows, offsets, cursors, destination);
        }
    }

    /// <summary>
    /// A variable-width child of a null parent collapses to ONE byte, so its natural encoding is
    /// built in scratch and copied only for the rows that keep it.
    /// </summary>
    private static void EncodeVariableChild(
        CanonicalArena arena, int childIndex, DType childType, RowSortField field,
        RowValidity parent, int rows,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        int[] sizes = ArrayPool<int>.Shared.Rent(rows);
        int[] scratchOffsets = ArrayPool<int>.Shared.Rent(rows);
        int[] scratchCursors = ArrayPool<int>.Shared.Rent(rows);
        byte[]? scratch = null;
        try
        {
            Span<int> childSizes = sizes.AsSpan(0, rows);
            childSizes.Clear();
            RowSizeKernel.Add(arena, childIndex, field, childSizes);

            Span<int> starts = scratchOffsets.AsSpan(0, rows);
            int total = Prefix(childSizes, starts);
            scratch = ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
            Span<int> zero = scratchCursors.AsSpan(0, rows);
            zero.Clear();
            RowEncodeKernel.Encode(arena, childIndex, field, starts, zero, scratch.AsSpan(0, total));

            byte childNull = RowSentinels.ChildCanonicalNull(childType, field);
            for (int i = 0; i < rows; i++)
            {
                int at = offsets[i] + cursors[i];
                if (parent.IsValid(i))
                {
                    scratch.AsSpan(starts[i], childSizes[i]).CopyTo(destination.Slice(at, childSizes[i]));
                    cursors[i] += childSizes[i];
                }
                else
                {
                    destination[at] = childNull;
                    cursors[i]++;
                }
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }

            ArrayPool<int>.Shared.Return(scratchCursors);
            ArrayPool<int>.Shared.Return(scratchOffsets);
            ArrayPool<int>.Shared.Return(sizes);
        }
    }

    private static void EncodeFixedSizeList(
        CanonicalArena arena, CanonicalNode node, RowSortField field,
        ReadOnlySpan<int> offsets, Span<int> cursors, Span<byte> destination)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        int rows = node.Length;
        int listSize = checked((int)node.FixedSize);
        int elementsIndex = node.ElementsIndex;
        CanonicalNode elements = arena.GetNode(elementsIndex);
        RequireLength(elements.Length, checked(rows * listSize), "fixed-size list elements");
        DType elementType = elements.DType;
        byte nullByte = RowSentinels.FixedNull(field);

        for (int i = 0; i < rows; i++)
        {
            destination[offsets[i] + cursors[i]] =
                validity.IsValid(i) ? RowSentinels.FixedNonNull : nullByte;
            cursors[i]++;
        }

        RowWidth width = RowWidths.For(elementType);
        byte childNull = RowSentinels.ChildCanonicalNull(elementType, field);
        int elementCount = checked(rows * listSize);
        int[] elementOffsets = ArrayPool<int>.Shared.Rent(Math.Max(elementCount, 1));
        int[] elementCursors = ArrayPool<int>.Shared.Rent(Math.Max(elementCount, 1));
        try
        {
            Span<int> starts = elementOffsets.AsSpan(0, elementCount);
            Span<int> zero = elementCursors.AsSpan(0, elementCount);
            zero.Clear();

            if (width.IsFixed)
            {
                // The elements array is one flat column of rows*listSize values, so it encodes in
                // a single pass at arithmetic offsets rather than row by row.
                int body = checked(width.Width * listSize);
                for (int i = 0; i < rows; i++)
                {
                    int at = offsets[i] + cursors[i];
                    for (int j = 0; j < listSize; j++)
                    {
                        starts[(i * listSize) + j] = at + (j * width.Width);
                    }
                }

                Encode(arena, elementsIndex, field, starts, zero, destination);
                for (int i = 0; i < rows; i++)
                {
                    cursors[i] += body;
                }

                for (int i = 0; i < rows; i++)
                {
                    if (validity.IsValid(i))
                    {
                        continue;
                    }

                    int start = offsets[i] + cursors[i] - body;
                    for (int j = 0; j < listSize; j++)
                    {
                        int slot = start + (j * width.Width);
                        destination[slot] = childNull;
                        destination.Slice(slot + 1, width.Width - 1).Clear();
                    }
                }

                return;
            }

            int[] sizes = ArrayPool<int>.Shared.Rent(Math.Max(elementCount, 1));
            byte[]? scratch = null;
            try
            {
                Span<int> elementSizes = sizes.AsSpan(0, elementCount);
                elementSizes.Clear();
                RowSizeKernel.Add(arena, elementsIndex, field, elementSizes);
                int total = Prefix(elementSizes, starts);
                scratch = ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
                Encode(arena, elementsIndex, field, starts, zero, scratch.AsSpan(0, total));

                for (int i = 0; i < rows; i++)
                {
                    int at = offsets[i] + cursors[i];
                    if (validity.IsValid(i))
                    {
                        int written = 0;
                        for (int j = 0; j < listSize; j++)
                        {
                            int k = (i * listSize) + j;
                            scratch.AsSpan(starts[k], elementSizes[k])
                                .CopyTo(destination.Slice(at + written, elementSizes[k]));
                            written += elementSizes[k];
                        }

                        cursors[i] += written;
                    }
                    else
                    {
                        // One null sentinel per element, so a null list is as wide as it is deep
                        // and two null lists of the same schema are byte-equal.
                        destination.Slice(at, listSize).Fill(childNull);
                        cursors[i] += listSize;
                    }
                }
            }
            finally
            {
                if (scratch is not null)
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }

                ArrayPool<int>.Shared.Return(sizes);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(elementCursors);
            ArrayPool<int>.Shared.Return(elementOffsets);
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>Exclusive prefix sum; returns the total.</summary>
    internal static int Prefix(ReadOnlySpan<int> sizes, Span<int> starts)
    {
        int accumulator = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            starts[i] = accumulator;
            int next = accumulator + sizes[i];
            if (next < accumulator)
            {
                throw new VortexFormatException("The row-encoded output exceeds 2 GiB.");
            }

            accumulator = next;
        }

        return accumulator;
    }

    /// <summary>
    /// Writes one ordered value big-endian, complemented when descending.
    /// </summary>
    /// <remarks>
    /// TryWriteBigEndian rather than WriteBigEndian ON PURPOSE. The latter is a DEFAULT INTERFACE
    /// METHOD that the primitive types do not override, so a constrained call to it has to box the
    /// receiver to reach the interface's implementation - 24 bytes per value, on every row of
    /// every fixed-width column. TryWriteBigEndian is abstract and implemented by each type, so
    /// the same call devirtualizes and allocates nothing. The two spell the same bytes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<T>(T ordered, bool descending, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (descending)
        {
            ordered = ~ordered;
        }

        if (!ordered.TryWriteBigEndian(destination, out int written) || written != destination.Length)
        {
            throw new VortexFormatException(
                $"A {destination.Length}-byte value slot took {written} bytes.");
        }
    }

    /// <summary>
    /// A child whose length disagrees with its parent's would desynchronize the cursors, so it is
    /// refused here rather than discovered as a corrupt row several columns later.
    /// </summary>
    private static void RequireLength(int actual, int expected, string what)
    {
        if (actual != expected)
        {
            throw new VortexFormatException(
                $"A {what} array holds {actual} values where its parent needs {expected}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GetBit(ReadOnlySpan<byte> bits, int bitIndex)
    {
        int byteIndex = bitIndex >> 3;
        if ((uint)byteIndex >= (uint)bits.Length)
        {
            throw new VortexFormatException($"Bit {bitIndex} is outside a {bits.Length}-byte bitmap.");
        }

        return (bits[byteIndex] & (1 << (bitIndex & 7))) != 0;
    }

    private static Int128 ReadStorage(ReadOnlySpan<byte> storage, int width, int row)
    {
        int at = row * width;
        switch (width)
        {
            case 1:
                return (sbyte)storage[at];
            case 2:
                return BinaryPrimitives.ReadInt16LittleEndian(storage.Slice(at, 2));
            case 4:
                return BinaryPrimitives.ReadInt32LittleEndian(storage.Slice(at, 4));
            case 8:
                return BinaryPrimitives.ReadInt64LittleEndian(storage.Slice(at, 8));
            default:
                return BinaryPrimitives.ReadInt128LittleEndian(storage.Slice(at, 16));
        }
    }

    private static bool FitsKey(Int128 value, int keyWidth)
    {
        if (keyWidth == 16)
        {
            return true;
        }

        Int128 limit = Int128.One << ((keyWidth * 8) - 1);
        return value >= -limit && value < limit;
    }

    private static ReadOnlySpan<byte> ResolveView(
        CanonicalNode node, ReadOnlySpan<byte> view, int length, int row)
    {
        if (length <= 12)
        {
            return view.Slice(4, length);
        }

        uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(8, 4));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        if (bufferIndex > int.MaxValue)
        {
            throw new VortexFormatException($"Row {row} names data buffer {bufferIndex}.");
        }

        VortexBuffer data = node.GetDataBuffer((int)bufferIndex);
        if ((ulong)offset + (ulong)(uint)length > (ulong)(uint)data.Length)
        {
            throw new VortexFormatException(
                $"Row {row} spans [{offset}, {(ulong)offset + (ulong)length}) of a data buffer " +
                $"holding {data.Length} bytes.");
        }

        return data.Span.Slice((int)offset, length);
    }
}
