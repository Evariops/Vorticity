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

/// <summary>
/// The write pass. Every encoder writes row <c>i</c> at <c>offsets[i] + cursors[i]</c> and advances
/// <c>cursors[i]</c> by exactly the contribution the sizing pass computed for the same column, so
/// the cursors end as the output's sizes and a column that writes more than its size runs off
/// the end rather than overlapping a neighbour. Encoding is column-at-a-time, which hoists the
/// branches on validity and on type out of the inner loop.
/// </summary>
internal static class RowEncodeKernel
{
    /// <summary>
    /// Encodes one column into the per-row slots named by the offsets and cursors, advancing the
    /// cursors. Every child inherits <paramref name="field"/> unchanged.
    /// </summary>
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

            // A row's key is its value, so an encoded column is encoded from its decoded twin.
            case CanonicalKind.Dictionary:
            case CanonicalKind.RunEnd:
                Encode(arena, arena.MaterializeEncoded(nodeIndex), field, offsets, cursors, destination);
                return;

            default:
                throw RowThrow.UnsupportedCanonical(node);
        }
    }

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
    /// complemented when descending. A null zero-fills its value bytes even under
    /// <c>descending</c>, since the fill is not a value and inverting it would make two nulls of
    /// the same column differ.
    /// </summary>
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
    /// Decimal: the signed-integer encoding of the unscaled value, at the width the declared
    /// precision implies rather than this chunk's storage width, which can differ from chunk to
    /// chunk of one column. A null slot's backing bytes are unspecified and may not fit the key
    /// width, so the fit check applies to valid rows only.
    /// </summary>
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
                // non-empty, or a following column's bytes line up against another row's padding.
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
                // Encode every row, then overwrite the null parents: the correction canonicalizes
                // the null body, so two null parents encode byte-equal whatever their children
                // hold, and the cursor arithmetic stays in one place.
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
    /// A variable-width child of a null parent collapses to one byte, so its natural encoding is
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
    /// Writes one ordered value big-endian, complemented when descending. TryWriteBigEndian rather
    /// than WriteBigEndian: the latter is a default interface method the primitive types do not
    /// override, so a constrained call to it boxes the receiver on every value.
    /// </summary>
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
