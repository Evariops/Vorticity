using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity;

/// <summary>Reads the values of one decoded node, for the accessors of <see cref="Column{T}"/>.</summary>
internal static class ColumnData
{
    private const int ViewSize = 16;
    private const int MaxInlineLength = 12;
    private const int ValuesAlignment = 64;

    /// <summary>The contiguous value bytes of a primitive or decimal node, 64-byte aligned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> Values(CanonicalArena arena, int node)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        if (record.Kind is not (CanonicalKind.Primitive or CanonicalKind.Decimal))
        {
            node = EncodedForms.Canonical(arena, node);
            record = ref arena.RecordRef(node);
        }

        ReadOnlySpan<byte> values = record.BufferA.Span;
        return IsAligned(values) ? values : Realign(arena, node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsAligned(ReadOnlySpan<byte> values) =>
        ((nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(values)) & (ValuesAlignment - 1)) == 0;

    /// <summary>
    /// Copies a node's values into an aligned block of its arena and repoints the node at it, once:
    /// a buffer read in place from a segment is only as aligned as the file laid the segment out.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReadOnlySpan<byte> Realign(CanonicalArena arena, int node)
    {
        ReadOnlySpan<byte> source = arena.RecordRef(node).BufferA.Span;
        VortexBuffer copy = arena.AllocateUninitialized(source.Length, ValuesAlignment, out Span<byte> destination);
        source.CopyTo(destination);
        arena.RecordRefMutable(node).BufferA = copy;
        return copy.Span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<T> Values<T>(CanonicalArena arena, int node)
        where T : struct =>
        MemoryMarshal.Cast<byte, T>(Values(arena, node));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckRow(int index, int length)
    {
        if ((uint)index >= (uint)length)
        {
            ThrowRow(index, length);
        }
    }

    internal static void ThrowRow(int index, int length) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The column has {length} rows.");

    /// <summary>The bytes of row <paramref name="index"/> of a text or binary node; empty for a null.</summary>
    internal static ReadOnlySpan<byte> Bytes(CanonicalArena arena, int node, int index)
    {
        int views = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(views);
        CheckRow(index, record.Length);
        if (!ArenaWords.IsValid(arena, views, index))
        {
            return default;
        }

        ReadOnlySpan<byte> view = record.Views.Span.Slice(index * ViewSize, ViewSize);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= MaxInlineLength)
        {
            return view.Slice(4, (int)size);
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        VortexBuffer data = record.GetDataBuffer(buffer);
        if ((ulong)offset + size > (ulong)(uint)data.Length)
        {
            throw new VortexFormatException($"Row {index} spans [{offset}, {(ulong)offset + size}) of a data buffer holding {data.Length} bytes.");
        }

        return data.Span.Slice((int)offset, (int)size);
    }

    /// <summary>The byte length of row <paramref name="index"/> of a text or binary node; 0 for a null.</summary>
    internal static int ByteLength(CanonicalArena arena, int node, int index)
    {
        int views = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(views);
        CheckRow(index, record.Length);
        return ArenaWords.IsValid(arena, views, index)
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Views.Span.Slice(index * ViewSize, 4))
            : 0;
    }

    internal static string? String(CanonicalArena arena, int node, int index) =>
        ArenaWords.IsValid(arena, node, index) ? Encoding.UTF8.GetString(Bytes(arena, node, index)) : null;

    /// <summary>The elements of row <paramref name="index"/> of a list node, as a range of its elements child.</summary>
    internal static Range ListRange(CanonicalArena arena, int node, int index)
    {
        CanonicalNode list = arena.GetNode(node);
        CheckRow(index, list.Length);
        if (list.Kind == CanonicalKind.FixedSizeList)
        {
            int size = (int)list.FixedSize;
            return new Range(index * size, (index + 1) * size);
        }

        long offset = Integer(list.Offsets.Span, list.OffsetPType, index);
        long length = Integer(list.Sizes.Span, list.SizePType, index);
        return new Range((int)offset, (int)(offset + length));
    }

    /// <summary>The elements child of a list node.</summary>
    internal static int Elements(CanonicalArena arena, int node) => arena.GetNode(node).ElementsIndex;

    /// <summary>The unscaled value of row <paramref name="index"/> of a decimal node, up to 128 bits.</summary>
    internal static Int128 Unscaled128(CanonicalArena arena, int node, int index)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        CheckRow(index, record.Length);
        ReadOnlySpan<byte> bytes = record.Values.Span;
        return record.Storage switch
        {
            DecimalStorageType.I8 => (sbyte)bytes[index],
            DecimalStorageType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes[(index * 2)..]),
            DecimalStorageType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * 4)..]),
            DecimalStorageType.I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes[(index * 8)..]),
            DecimalStorageType.I128 => BinaryPrimitives.ReadInt128LittleEndian(bytes[(index * 16)..]),
            _ => throw new VortexSchemaException("A 256-bit decimal is read as VortexDecimal."),
        };
    }

    /// <summary>Row <paramref name="index"/> of a decimal node as a <see cref="VortexDecimal"/>.</summary>
    internal static VortexDecimal Wide(CanonicalArena arena, int node, int index)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        CheckRow(index, record.Length);
        if (record.Storage != DecimalStorageType.I256)
        {
            return VortexDecimal.FromInt128(Unscaled128(arena, values, index), record.Precision, record.Scale);
        }

        return new VortexDecimal(Int256.FromLittleEndianBytes(record.Values.Span.Slice(index * 32, 32)), record.Precision, record.Scale);
    }

    /// <summary>Row <paramref name="index"/> of a decimal node of at most 28 digits as a <see cref="decimal"/>.</summary>
    internal static decimal Decimal(CanonicalArena arena, int node, int index)
    {
        Int128 unscaled = Unscaled128(arena, node, index);
        int scale = arena.GetNode(EncodedForms.Canonical(arena, node)).Scale;
        bool negative = unscaled < 0;
        UInt128 magnitude = (UInt128)(negative ? -unscaled : unscaled);
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)scale);
    }

    /// <summary>The storage value of row <paramref name="index"/> of an integer node, widened to 64 bits.</summary>
    internal static long Int64(CanonicalArena arena, int node, int index)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        CheckRow(index, record.Length);
        return Integer(record.Values.Span, record.PType, index);
    }

    /// <summary>Sixteen bytes of row <paramref name="index"/> of a uuid's fixed-size list.</summary>
    internal static Guid Guid(CanonicalArena arena, int node, int index)
    {
        CanonicalNode list = arena.GetNode(node);
        CheckRow(index, list.Length);
        ReadOnlySpan<byte> bytes = Values(arena, list.ElementsIndex);
        return new Guid(bytes.Slice(index * 16, 16), bigEndian: true);
    }

    // ---- whole-column copies: the node resolved once, then a loop over the rows -------------------
    //
    // The indexers above resolve the node, its canonical twin and its validity on every call, which
    // a loop over a batch pays once per row. A copy resolves them once, so that a record's ReadRows
    // costs a conversion per row and nothing else.

    /// <summary><paramref name="destination"/> cut to <paramref name="length"/> rows, which it must hold.</summary>
    /// <exception cref="ArgumentException">The destination is shorter than the column.</exception>
    internal static Span<T> Destination<T>(Span<T> destination, int length)
    {
        if (destination.Length < length)
        {
            throw new ArgumentException($"The destination holds {destination.Length} rows; the column has {length}.", nameof(destination));
        }

        return destination[..length];
    }

    /// <summary>Whether row <paramref name="index"/> is valid by <paramref name="valid"/>, which is empty when every row is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValid(ReadOnlySpan<ulong> valid, int index) =>
        valid.IsEmpty || ((valid[index >> 6] >> (index & 63)) & 1UL) != 0;

    /// <summary>Copies the values of a nullable numeric node, a null for a null row.</summary>
    internal static void CopyNullable<T>(CanonicalArena arena, int node, Span<T?> destination)
        where T : unmanaged
    {
        ReadOnlySpan<T> values = Values<T>(arena, node);
        Span<T?> into = Destination(destination, values.Length);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        if (valid.IsEmpty)
        {
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = values[i];
            }

            return;
        }

        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? values[i] : null;
        }
    }

    /// <summary>Copies the values of a bool node.</summary>
    internal static void CopyBits(CanonicalArena arena, int valuesNode, Span<bool> destination)
    {
        int length = arena.GetNode(valuesNode).Length;
        ReadOnlySpan<ulong> bits = ArenaWords.Bits(arena, valuesNode);
        Span<bool> into = Destination(destination, length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = ((bits[i >> 6] >> (i & 63)) & 1UL) != 0;
        }
    }

    /// <summary>Copies the values of a nullable bool node, a null for a null row.</summary>
    internal static void CopyBits(CanonicalArena arena, int node, int valuesNode, Span<bool?> destination)
    {
        int length = arena.GetNode(valuesNode).Length;
        ReadOnlySpan<ulong> bits = ArenaWords.Bits(arena, valuesNode);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        Span<bool?> into = Destination(destination, length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? ((bits[i >> 6] >> (i & 63)) & 1UL) != 0 : null;
        }
    }

    /// <summary>Copies the rows of a decimal node of at most 28 digits.</summary>
    internal static void CopyDecimals(CanonicalArena arena, int node, Span<decimal> destination)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        ReadOnlySpan<byte> bytes = record.Values.Span;
        Span<decimal> into = Destination(destination, record.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = ToDecimal(Unscaled(bytes, record.Storage, i), record.Scale);
        }
    }

    /// <summary>Copies the rows of a nullable decimal node of at most 28 digits, a null for a null row.</summary>
    internal static void CopyDecimals(CanonicalArena arena, int node, Span<decimal?> destination)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        ReadOnlySpan<byte> bytes = record.Values.Span;
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        Span<decimal?> into = Destination(destination, record.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? ToDecimal(Unscaled(bytes, record.Storage, i), record.Scale) : null;
        }
    }

    /// <summary>Copies the rows of a decimal node as <see cref="VortexDecimal"/>.</summary>
    internal static void CopyWide(CanonicalArena arena, int node, Span<VortexDecimal> destination)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        Span<VortexDecimal> into = Destination(destination, record.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = WideAt(in record, i);
        }
    }

    /// <summary>Copies the rows of a nullable decimal node as <see cref="VortexDecimal"/>, a null for a null row.</summary>
    internal static void CopyWide(CanonicalArena arena, int node, Span<VortexDecimal?> destination)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        Span<VortexDecimal?> into = Destination(destination, record.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? WideAt(in record, i) : null;
        }
    }

    /// <summary>Copies the rows of a uuid node.</summary>
    internal static void CopyGuids(CanonicalArena arena, int node, Span<Guid> destination)
    {
        CanonicalNode list = arena.GetNode(node);
        ReadOnlySpan<byte> bytes = Values(arena, list.ElementsIndex);
        Span<Guid> into = Destination(destination, list.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = new Guid(bytes.Slice(i * 16, 16), bigEndian: true);
        }
    }

    /// <summary>Copies the rows of a nullable uuid node, a null for a null row.</summary>
    internal static void CopyGuids(CanonicalArena arena, int node, Span<Guid?> destination)
    {
        CanonicalNode list = arena.GetNode(node);
        ReadOnlySpan<byte> bytes = Values(arena, list.ElementsIndex);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        Span<Guid?> into = Destination(destination, list.Length);
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = IsValid(valid, i) ? new Guid(bytes.Slice(i * 16, 16), bigEndian: true) : null;
        }
    }

    /// <summary>Copies the rows of a text node as strings, a null for a null row.</summary>
    /// <typeparam name="TText"><see cref="string"/>, with or without its nullability: the text column's type argument.</typeparam>
    internal static void CopyStrings<TText>(CanonicalArena arena, int node, Span<TText> destination)
    {
        int views = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(views);
        ReadOnlySpan<byte> viewBytes = record.Views.Span;
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, views);
        Span<TText> into = Destination(destination, record.Length);
        for (int i = 0; i < into.Length; i++)
        {
            string? text = IsValid(valid, i) ? Encoding.UTF8.GetString(ViewBytes(in record, viewBytes, i)) : null;
            into[i] = Unsafe.As<string?, TText>(ref text);
        }
    }

    /// <summary>The integer storage of a node, resolved once for a loop over its rows.</summary>
    internal readonly ref struct IntegerStorage
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly PType _ptype;

        internal IntegerStorage(ReadOnlySpan<byte> bytes, PType ptype, int length)
        {
            _bytes = bytes;
            _ptype = ptype;
            Length = length;
        }

        internal int Length { get; }

        internal long this[int index] => Integer(_bytes, _ptype, index);
    }

    /// <summary>The integer storage of <paramref name="node"/>, made canonical once.</summary>
    internal static IntegerStorage Integers(CanonicalArena arena, int node)
    {
        int values = EncodedForms.Canonical(arena, node);
        CanonicalNode record = arena.GetNode(values);
        return new IntegerStorage(record.Values.Span, record.PType, record.Length);
    }

    /// <summary>The bytes of row <paramref name="index"/> of a canonical text node, which must be valid.</summary>
    private static ReadOnlySpan<byte> ViewBytes(in CanonicalNode record, ReadOnlySpan<byte> views, int index)
    {
        ReadOnlySpan<byte> view = views.Slice(index * ViewSize, ViewSize);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= MaxInlineLength)
        {
            return view.Slice(4, (int)size);
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        VortexBuffer data = record.GetDataBuffer(buffer);
        if ((ulong)offset + size > (ulong)(uint)data.Length)
        {
            throw new VortexFormatException($"Row {index} spans [{offset}, {(ulong)offset + size}) of a data buffer holding {data.Length} bytes.");
        }

        return data.Span.Slice((int)offset, (int)size);
    }

    /// <summary>The unscaled value at <paramref name="index"/> of a decimal node's storage, up to 128 bits.</summary>
    private static Int128 Unscaled(ReadOnlySpan<byte> bytes, DecimalStorageType storage, int index) => storage switch
    {
        DecimalStorageType.I8 => (sbyte)bytes[index],
        DecimalStorageType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes[(index * 2)..]),
        DecimalStorageType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * 4)..]),
        DecimalStorageType.I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes[(index * 8)..]),
        DecimalStorageType.I128 => BinaryPrimitives.ReadInt128LittleEndian(bytes[(index * 16)..]),
        _ => throw new VortexSchemaException("A 256-bit decimal is read as VortexDecimal."),
    };

    private static decimal ToDecimal(Int128 unscaled, int scale)
    {
        bool negative = unscaled < 0;
        UInt128 magnitude = (UInt128)(negative ? -unscaled : unscaled);
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)scale);
    }

    private static VortexDecimal WideAt(in CanonicalNode record, int index)
    {
        ReadOnlySpan<byte> bytes = record.Values.Span;
        return record.Storage == DecimalStorageType.I256
            ? new VortexDecimal(Int256.FromLittleEndianBytes(bytes.Slice(index * 32, 32)), record.Precision, record.Scale)
            : VortexDecimal.FromInt128(Unscaled(bytes, record.Storage, index), record.Precision, record.Scale);
    }

    private static long Integer(ReadOnlySpan<byte> buffer, PType ptype, int index) => ptype switch
    {
        PType.U8 => buffer[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(buffer[(index * 2)..]),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(buffer[(index * 4)..]),
        PType.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[(index * 8)..]),
        PType.I8 => (sbyte)buffer[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(buffer[(index * 2)..]),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(buffer[(index * 4)..]),
        PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(buffer[(index * 8)..]),
        _ => throw new VortexFormatException($"A {ptype.Name()} buffer is not integers."),
    };
}
