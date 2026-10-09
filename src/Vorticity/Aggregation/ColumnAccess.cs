using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>How an aggregate reads a column's values.</summary>
internal enum StorageKind : byte
{
    Unsupported,

    /// <summary>A primitive, or a date, time, timestamp or extension stored as one.</summary>
    Primitive,

    /// <summary>A decimal of at most 38 digits, read as its unscaled value in 128 bits whatever width a block stores.</summary>
    Decimal,

    /// <summary>A uuid, read as its sixteen bytes in big-endian order, so that the numeric order is the byte order.</summary>
    Uuid,

    Bool,

    /// <summary>Text or binary.</summary>
    Bytes,

    /// <summary>A decimal of more than 38 digits, read as its unscaled value in 256 bits whatever width a block stores.</summary>
    Decimal256,
}

/// <summary>A column an aggregate or a key reads, with what its type makes of it.</summary>
internal sealed class ColumnShape
{
    internal ColumnShape(ColumnSym column)
    {
        Column = column;
        Type = column.Type;
        (Kind, PType) = Classify(column.Type);
    }

    internal ColumnSym Column { get; }

    /// <summary>The column's type, as the file declares it.</summary>
    internal VortexType Type { get; }

    internal StorageKind Kind { get; }

    /// <summary>The primitive type of a <see cref="StorageKind.Primitive"/> column.</summary>
    internal PType PType { get; }

    internal FieldExpr Field => Column.Field;

    internal string Path => Column.Field.Path;

    /// <summary>What makes two shapes one value: the column's path, and the function a value expression applies to it.</summary>
    internal string Key => Column.Field.Key;

    /// <summary>Whether both shapes read the same values: the same column through the same functions.</summary>
    internal bool Is(ColumnShape other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    internal VortexUnsupportedException Unsupported(string what) =>
        new VortexUnsupportedException(Type.ToString(), ComponentKind.Feature, $"'{Path}' is {Type}, which {what} does not read.");

    private static (StorageKind Kind, PType PType) Classify(VortexType type)
    {
        while (type.Kind == VortexTypeKind.Extension)
        {
            if (type.ExtensionId == ExtensionIds.Uuid)
            {
                return (StorageKind.Uuid, default);
            }

            if (type.StorageType is not { } storage)
            {
                return (StorageKind.Unsupported, default);
            }

            type = storage;
        }

        return type.Kind switch
        {
            VortexTypeKind.Primitive => (StorageKind.Primitive, type.PrimitiveType),
            VortexTypeKind.Decimal => (type.Precision <= 38 ? StorageKind.Decimal : StorageKind.Decimal256, default),
            VortexTypeKind.Bool => (StorageKind.Bool, default),
            VortexTypeKind.Utf8 or VortexTypeKind.Binary => (StorageKind.Bytes, default),
            _ => (StorageKind.Unsupported, default),
        };
    }
}

/// <summary>
/// The values of a node as a reader's storage type, for a reader that reads the same node at every
/// range a batch is folded in: read once a batch, the view onto the node kept, or the copy when the
/// node has to be widened, a decimal narrower than 128 bits or a uuid, and read from there at the
/// batch's other ranges. A copy of retained values also serves the later batches that view them.
/// </summary>
internal struct ValuesCache<TValue>
    where TValue : unmanaged
{
    private TValue[]? _values;
    private VortexBuffer _view;
    private int _length;
    private long _batch;
    private int _node;
    private int _canonical;
    private CanonicalOrigin _origin;
    private bool _widened;

    /// <summary>The values of <paramref name="node"/> as <paramref name="kind"/>, and their validity.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="batch">The batch's number, from 1, or 0 for a reader that keys nothing on it.</param>
    /// <param name="node">The node.</param>
    /// <param name="kind">The storage type.</param>
    /// <param name="validity">The node's validity words, empty when every value is valid.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<TValue> Of(CanonicalArena arena, long batch, int node, StorageKind kind, out ReadOnlySpan<ulong> validity)
    {
        if (batch != 0 && batch == _batch && node == _node)
        {
            validity = ArenaWords.Validity(arena, _canonical);
            return _widened ? _values.AsSpan(0, _length) : MemoryMarshal.Cast<byte, TValue>(_view.Span)[.._length];
        }

        return Read(arena, batch, node, kind, out validity);
    }

    /// <summary>The first read of the node in this batch: the copy retained values had, or a read of the node.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ReadOnlySpan<TValue> Read(CanonicalArena arena, long batch, int node, StorageKind kind, out ReadOnlySpan<ulong> validity)
    {
        int canonical = EncodedForms.Canonical(arena, node);
        CanonicalOrigin origin = arena.OriginOf(node);
        ReadOnlySpan<TValue> values;
        if (_widened && origin.IsKnown && origin == _origin)
        {
            validity = ArenaWords.Validity(arena, canonical);
            values = _values.AsSpan(0, _length);
        }
        else
        {
            _values ??= [];
            values = FixedReader.Values(arena, node, kind, ref _values, out validity);
            _widened = FixedReader.Widens(arena, node, kind);
            _view = _widened ? default : arena.RecordRef(canonical).BufferA;
            _length = values.Length;
            _origin = origin;
        }

        _batch = batch;
        _node = node;
        _canonical = canonical;
        return values;
    }
}

/// <summary>The values of a fixed-width column as the aggregate's storage type, in whatever form a block holds them.</summary>
internal static class FixedReader
{
    /// <summary>The values of <paramref name="node"/> in canonical form, decoding it when it is encoded.</summary>
    internal static ReadOnlySpan<TValue> Values<TValue>(CanonicalArena arena, int node, StorageKind kind, scoped ref TValue[] scratch, out ReadOnlySpan<ulong> validity)
        where TValue : unmanaged
    {
        int canonical = EncodedForms.Canonical(arena, node);
        validity = ArenaWords.Validity(arena, canonical);
        ref readonly CanonicalRecord record = ref arena.RecordRef(canonical);
        int length = record.Length;
        switch (kind)
        {
            case StorageKind.Decimal:
            {
                if (record.Storage == DecimalStorageType.I128)
                {
                    return MemoryMarshal.Cast<byte, TValue>(record.BufferA.Span)[..length];
                }

                Scratch.Grow(ref scratch, length);
                Span<Int128> into = MemoryMarshal.Cast<TValue, Int128>(scratch.AsSpan(0, length));
                WidenDecimals(record.BufferA.Span, record.Storage, into);
                return scratch.AsSpan(0, length);
            }

            case StorageKind.Decimal256:
            {
                if (record.Storage == DecimalStorageType.I256)
                {
                    return MemoryMarshal.Cast<byte, TValue>(record.BufferA.Span)[..length];
                }

                // A precision above 38 is stored in 256 bits, so a narrower block is a constant's
                // element or a writer's narrowing, widened here once a batch.
                Scratch.Grow(ref scratch, length);
                Span<Int256> into = MemoryMarshal.Cast<TValue, Int256>(scratch.AsSpan(0, length));
                WidenDecimals(record.BufferA.Span, record.Storage, into);
                return scratch.AsSpan(0, length);
            }

            case StorageKind.Uuid:
            {
                ReadOnlySpan<byte> bytes = ColumnData.Values(arena, arena.GetNode(canonical).ElementsIndex);
                Scratch.Grow(ref scratch, length);
                Span<UInt128> into = MemoryMarshal.Cast<TValue, UInt128>(scratch.AsSpan(0, length));
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = BinaryPrimitives.ReadUInt128BigEndian(bytes.Slice(i * 16, 16));
                }

                return scratch.AsSpan(0, length);
            }

            default:
                return MemoryMarshal.Cast<byte, TValue>(record.BufferA.Span)[..length];
        }
    }

    /// <summary>Whether <see cref="Values{TValue}"/> copies <paramref name="node"/> into a wider type rather than viewing it.</summary>
    internal static bool Widens(CanonicalArena arena, int node, StorageKind kind) =>
        kind == StorageKind.Uuid
        || (kind == StorageKind.Decimal && arena.RecordRef(EncodedForms.Canonical(arena, node)).Storage != DecimalStorageType.I128)
        || (kind == StorageKind.Decimal256 && arena.RecordRef(EncodedForms.Canonical(arena, node)).Storage != DecimalStorageType.I256);

    /// <summary>The one value of a constant node.</summary>
    internal static TValue Constant<TValue>(CanonicalArena arena, int node, StorageKind kind)
        where TValue : unmanaged
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        ReadOnlySpan<byte> element = record.BufferA.Span[..(int)record.FixedSize];
        if (kind == StorageKind.Decimal256)
        {
            Int256 wide = element.Length == Int256.ByteCount ? Int256.FromLittleEndianBytes(element) : new Int256(Narrow(element));
            return Unsafe.As<Int256, TValue>(ref wide);
        }

        if (kind != StorageKind.Decimal)
        {
            return MemoryMarshal.Read<TValue>(element);
        }

        Int128 value = Narrow(element);
        return Unsafe.As<Int128, TValue>(ref value);
    }

    /// <summary>A decimal element of at most 128 bits, sign-extended.</summary>
    private static Int128 Narrow(ReadOnlySpan<byte> element) => element.Length switch
    {
        1 => (sbyte)element[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(element),
        4 => BinaryPrimitives.ReadInt32LittleEndian(element),
        8 => BinaryPrimitives.ReadInt64LittleEndian(element),
        16 => BinaryPrimitives.ReadInt128LittleEndian(element),
        _ => throw new VortexFormatException($"A decimal of at most 38 digits is stored in 16 bytes or fewer, not {element.Length}."),
    };

    /// <summary>Whether a block's encoded form may be read directly for this kind of column.</summary>
    internal static ColumnEncoding EncodingOf(CanonicalArena arena, int node, StorageKind kind) =>
        kind == StorageKind.Uuid ? ColumnEncoding.Canonical : EncodedForms.EncodingOf(arena, node);

    private static void WidenDecimals(ReadOnlySpan<byte> bytes, DecimalStorageType storage, Span<Int256> into)
    {
        if (storage == DecimalStorageType.I128)
        {
            ReadOnlySpan<Int128> values = MemoryMarshal.Cast<byte, Int128>(bytes);
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = new Int256(values[i]);
            }

            return;
        }

        // The narrower widths through the 128-bit widening, a block at a time on the stack.
        Span<Int128> step = stackalloc Int128[256];
        for (int start = 0; start < into.Length; start += step.Length)
        {
            int count = Math.Min(step.Length, into.Length - start);
            int width = DecimalStorage.ByteWidth(storage);
            WidenDecimals(bytes.Slice(start * width, count * width), storage, step[..count]);
            for (int i = 0; i < count; i++)
            {
                into[start + i] = new Int256(step[i]);
            }
        }
    }

    private static void WidenDecimals(ReadOnlySpan<byte> bytes, DecimalStorageType storage, Span<Int128> into)
    {
        switch (storage)
        {
            case DecimalStorageType.I8:
            {
                ReadOnlySpan<sbyte> values = MemoryMarshal.Cast<byte, sbyte>(bytes);
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = values[i];
                }

                return;
            }

            case DecimalStorageType.I16:
            {
                ReadOnlySpan<short> values = MemoryMarshal.Cast<byte, short>(bytes);
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = values[i];
                }

                return;
            }

            case DecimalStorageType.I32:
            {
                ReadOnlySpan<int> values = MemoryMarshal.Cast<byte, int>(bytes);
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = values[i];
                }

                return;
            }

            case DecimalStorageType.I64:
            {
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < into.Length; i++)
                {
                    into[i] = values[i];
                }

                return;
            }

            default:
                throw new VortexFormatException($"A decimal of at most 38 digits is not stored as {storage}.");
        }
    }
}

/// <summary>The values of a text or binary block, one span per row, borrowed from the batch.</summary>
internal readonly ref struct BytesBlock
{
    private const int ViewSize = 16;
    private const int MaxInline = 12;

    private readonly CanonicalArena _arena;
    private readonly ReadOnlySpan<byte> _views;
    private readonly int _dataStart;
    private readonly int _dataCount;

    // The first data buffer, resolved once: the one a decoded block's values lie in, as a rule.
    private readonly ReadOnlySpan<byte> _first;

    private BytesBlock(CanonicalArena arena, ReadOnlySpan<byte> views, int dataStart, int dataCount, int length)
    {
        _arena = arena;
        _views = views;
        _dataStart = dataStart;
        _dataCount = dataCount;
        _first = dataCount > 0 ? arena.DataBufferAt(dataStart).Span : default;
        Length = length;
    }

    internal int Length { get; }

    /// <summary>The block of <paramref name="node"/> in canonical form, decoding it when it is encoded, and its validity.</summary>
    internal static BytesBlock Canonical(CanonicalArena arena, int node, out ReadOnlySpan<ulong> validity)
    {
        int views = EncodedForms.Canonical(arena, node);
        validity = ArenaWords.Validity(arena, views);
        ref readonly CanonicalRecord record = ref arena.RecordRef(views);
        return new BytesBlock(arena, record.BufferA.Span, record.DataBufferStart, record.DataBufferCount, record.Length);
    }

    /// <summary>A canonical block over its views and data buffers, resolved once by a caller that compares many of its values.</summary>
    internal static BytesBlock Over(CanonicalArena arena, ReadOnlySpan<byte> views, int dataStart, int dataCount, int length) =>
        new BytesBlock(arena, views, dataStart, dataCount, length);

    /// <summary>The one value of a constant node.</summary>
    internal static ReadOnlySpan<byte> Constant(CanonicalArena arena, int node)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        return record.BufferA.Span[..(int)record.FixedSize];
    }

    /// <summary>The bytes of row <paramref name="row"/>; undefined for a null row.</summary>
    /// <remarks>
    /// Inlined, its throws apart: a group by of texts reads it two or three times a row, and as a call
    /// it took a twelfth of the cycles of a million (url, integer) groups.
    /// </remarks>
    internal ReadOnlySpan<byte> this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ReadOnlySpan<byte> view = _views.Slice(row * ViewSize, ViewSize);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
            if (size <= MaxInline)
            {
                return view.Slice(4, (int)size);
            }

            uint buffer = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
            ReadOnlySpan<byte> data = buffer == 0 && _dataCount > 0 ? _first : Data(row, buffer);
            if ((ulong)offset + size > (ulong)(uint)data.Length)
            {
                ThrowPast(row, offset, size, data.Length);
            }

            return data.Slice((int)offset, (int)size);
        }
    }

    /// <summary>
    /// Row <paramref name="row"/>'s value as one word, when it is 12 bytes or less, which its view holds
    /// whole: its length in the low 32 bits, its bytes above, zero past them whatever the view holds there
    /// (<see cref="ShortTextKeys"/>); false for a longer value. Undefined for a null row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWord(int row, out TextWord word)
    {
        ReadOnlySpan<byte> view = _views.Slice(row * ViewSize, ViewSize);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(view);
        uint size = (uint)low;
        if (size > MaxInline)
        {
            word = default;
            return false;
        }

        // The value's bits from the view's start: 32 for its length, then 8 a byte, 32 to 128.
        int bits = 32 + (8 * (int)size);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(view[8..]);
        low &= bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        high &= bits <= 64 ? 0 : bits >= 128 ? ulong.MaxValue : (1UL << (bits - 64)) - 1;
        word = new TextWord(low, high);
        return true;
    }

    /// <summary>
    /// The words of rows <paramref name="start"/> on, as many as <paramref name="words"/> holds, every row
    /// valid, as <see cref="TryWord"/> makes each: the view's sixteen bytes, those past the value zeroed by a
    /// mask its length picks. False when a value is longer than 12 bytes, which the words cannot hold.
    /// </summary>
    /// <remarks>
    /// No branch on a row: the longest length is kept and judged once. Row by row through
    /// <see cref="TryWord"/>, two branches a row and the validity's test took a sixth of the cycles of a sum
    /// by a hundred short texts (2026-10-09).
    /// </remarks>
    internal bool TryWords(int start, Span<TextWord> words)
    {
        ReadOnlySpan<byte> views = _views.Slice(start * ViewSize, words.Length * ViewSize);
        ref byte view = ref MemoryMarshal.GetReference(views);
        ref TextWord word = ref MemoryMarshal.GetReference(words);
        ref ulong lows = ref MemoryMarshal.GetReference(LowMasks);
        ref ulong highs = ref MemoryMarshal.GetReference(HighMasks);
        uint longest = 0;
        for (int i = 0; i < words.Length; i++)
        {
            ref byte at = ref Unsafe.Add(ref view, i * ViewSize);
            ulong low = Unsafe.ReadUnaligned<ulong>(ref at);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref at, 8));
            uint size = (uint)low;
            longest = Math.Max(longest, size);
            nint masked = (nint)Math.Min(size, (uint)MaxInline);
            Unsafe.Add(ref word, i) = new TextWord(low & Unsafe.Add(ref lows, masked), high & Unsafe.Add(ref highs, masked));
        }

        return longest <= MaxInline;
    }

    // By length, from 0 to 12: the bits of a word's low half, then of its high half, that its length and its
    // bytes take, 32 then 8 a byte.
    private static ReadOnlySpan<ulong> LowMasks =>
    [
        0x0000_0000_FFFF_FFFFUL, 0x0000_00FF_FFFF_FFFFUL, 0x0000_FFFF_FFFF_FFFFUL, 0x00FF_FFFF_FFFF_FFFFUL,
        ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue,
        ulong.MaxValue, ulong.MaxValue,
    ];

    private static ReadOnlySpan<ulong> HighMasks =>
    [
        0, 0, 0, 0, 0, 0xFFUL, 0xFFFFUL, 0xFF_FFFFUL, 0xFFFF_FFFFUL, 0xFF_FFFF_FFFFUL, 0xFFFF_FFFF_FFFFUL,
        0xFF_FFFF_FFFF_FFFFUL, ulong.MaxValue,
    ];

    /// <summary>Data buffer <paramref name="buffer"/> of the block, which row <paramref name="row"/> names.</summary>
    private ReadOnlySpan<byte> Data(int row, uint buffer)
    {
        if (buffer >= (uint)_dataCount)
        {
            throw new VortexFormatException($"Row {row} names data buffer {buffer} of {_dataCount}.");
        }

        return _arena.DataBufferAt(_dataStart + (int)buffer).Span;
    }

    [DoesNotReturn]
    private static void ThrowPast(int row, uint offset, uint size, int length) =>
        throw new VortexFormatException($"Row {row} spans [{offset}, {(ulong)offset + size}) of a data buffer holding {length} bytes.");
}

/// <summary>Storage values turned into the .NET values a caller asked for.</summary>
internal static class StorageValues
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValid(ReadOnlySpan<ulong> validity, int index) =>
        validity.IsEmpty || ((validity[index >> 6] >> (index & 63)) & 1) != 0;

    /// <summary>A fixed-width storage value as <typeparamref name="T"/>: itself when it already is, through the column's literal otherwise.</summary>
    internal static T ToClr<TValue, T>(TValue value, ColumnShape shape)
        where TValue : unmanaged
    {
        if (typeof(T) == typeof(TValue))
        {
            return Unsafe.As<TValue, T>(ref value);
        }

        if (typeof(T) == typeof(TValue?))
        {
            TValue? nullable = value;
            return Unsafe.As<TValue?, T>(ref nullable);
        }

        // A decimal as a VortexDecimal, built from its unscaled value: a literal would pass it
        // through a BigInteger, one allocation per group of a grouped aggregate.
        if ((typeof(T) == typeof(VortexDecimal) || typeof(T) == typeof(VortexDecimal?))
            && (typeof(TValue) == typeof(Int128) || typeof(TValue) == typeof(Int256)))
        {
            Int256 unscaled = typeof(TValue) == typeof(Int128) ? new Int256(Unsafe.As<TValue, Int128>(ref value)) : Unsafe.As<TValue, Int256>(ref value);
            VortexDecimal wide = new VortexDecimal(unscaled, (byte)shape.Type.Precision, (sbyte)shape.Type.Scale);
            if (typeof(T) == typeof(VortexDecimal))
            {
                return Unsafe.As<VortexDecimal, T>(ref wide);
            }

            VortexDecimal? present = wide;
            return Unsafe.As<VortexDecimal?, T>(ref present);
        }

        return LiteralValues.ToValue<T>(Literal(value, shape), shape.Type)!;
    }

    /// <summary>Text or binary as <typeparamref name="T"/>, which copies.</summary>
    internal static T BytesToClr<T>(ReadOnlySpan<byte> value, ColumnShape shape) =>
        LiteralValues.ToValue<T>(FilterLiteral.From(value), shape.Type)!;

    /// <summary>A boolean as <typeparamref name="T"/>.</summary>
    internal static T BoolToClr<T>(bool value, ColumnShape shape)
    {
        if (typeof(T) == typeof(bool))
        {
            return Unsafe.As<bool, T>(ref value);
        }

        if (typeof(T) == typeof(bool?))
        {
            bool? nullable = value;
            return Unsafe.As<bool?, T>(ref nullable);
        }

        return LiteralValues.ToValue<T>(FilterLiteral.From(value), shape.Type)!;
    }

    /// <summary>The fixed-width value encoded at the start of <paramref name="bytes"/>, as <typeparamref name="T"/>.</summary>
    internal static T Read<T>(ReadOnlySpan<byte> bytes, ColumnShape shape)
    {
        switch (shape.Kind)
        {
            case StorageKind.Decimal:
                return ToClr<Int128, T>(MemoryMarshal.Read<Int128>(bytes), shape);
            case StorageKind.Decimal256:
                return ToClr<Int256, T>(Int256.FromLittleEndianBytes(bytes[..Int256.ByteCount]), shape);
            case StorageKind.Uuid:
                return ToClr<UInt128, T>(BinaryPrimitives.ReadUInt128BigEndian(bytes), shape);
            default:
                return shape.PType switch
                {
                    PType.I8 => ToClr<sbyte, T>(MemoryMarshal.Read<sbyte>(bytes), shape),
                    PType.I16 => ToClr<short, T>(MemoryMarshal.Read<short>(bytes), shape),
                    PType.I32 => ToClr<int, T>(MemoryMarshal.Read<int>(bytes), shape),
                    PType.I64 => ToClr<long, T>(MemoryMarshal.Read<long>(bytes), shape),
                    PType.U8 => ToClr<byte, T>(MemoryMarshal.Read<byte>(bytes), shape),
                    PType.U16 => ToClr<ushort, T>(MemoryMarshal.Read<ushort>(bytes), shape),
                    PType.U32 => ToClr<uint, T>(MemoryMarshal.Read<uint>(bytes), shape),
                    PType.U64 => ToClr<ulong, T>(MemoryMarshal.Read<ulong>(bytes), shape),
                    PType.F16 => ToClr<Half, T>(MemoryMarshal.Read<Half>(bytes), shape),
                    PType.F32 => ToClr<float, T>(MemoryMarshal.Read<float>(bytes), shape),
                    _ => ToClr<double, T>(MemoryMarshal.Read<double>(bytes), shape),
                };
        }
    }

    /// <summary>The width a fixed-width key part takes in an encoded key.</summary>
    internal static int Width(ColumnShape shape) => shape.Kind switch
    {
        StorageKind.Decimal or StorageKind.Uuid => 16,
        StorageKind.Decimal256 => Int256.ByteCount,
        _ => shape.PType.ByteWidth(),
    };

    private static FilterLiteral Literal<TValue>(TValue value, ColumnShape shape)
        where TValue : unmanaged
    {
        switch (shape.Kind)
        {
            case StorageKind.Decimal:
                return SymLowering.WideLiteral((BigInteger)Unsafe.As<TValue, Int128>(ref value));
            case StorageKind.Decimal256:
            {
                Span<byte> wide = stackalloc byte[Int256.ByteCount];
                Unsafe.As<TValue, Int256>(ref value).WriteLittleEndianBytes(wide);
                return SymLowering.WideLiteral(new BigInteger(wide, isUnsigned: false, isBigEndian: false));
            }

            case StorageKind.Uuid:
            {
                Span<byte> bytes = stackalloc byte[16];
                BinaryPrimitives.WriteUInt128BigEndian(bytes, Unsafe.As<TValue, UInt128>(ref value));
                return FilterLiteral.From(bytes);
            }

            default:
                return shape.PType switch
                {
                    PType.I8 => FilterLiteral.From((long)Unsafe.As<TValue, sbyte>(ref value)),
                    PType.I16 => FilterLiteral.From((long)Unsafe.As<TValue, short>(ref value)),
                    PType.I32 => FilterLiteral.From((long)Unsafe.As<TValue, int>(ref value)),
                    PType.I64 => FilterLiteral.From(Unsafe.As<TValue, long>(ref value)),
                    PType.U8 => FilterLiteral.From((ulong)Unsafe.As<TValue, byte>(ref value)),
                    PType.U16 => FilterLiteral.From((ulong)Unsafe.As<TValue, ushort>(ref value)),
                    PType.U32 => FilterLiteral.From((ulong)Unsafe.As<TValue, uint>(ref value)),
                    PType.U64 => FilterLiteral.From(Unsafe.As<TValue, ulong>(ref value)),
                    PType.F16 => FilterLiteral.From((double)Unsafe.As<TValue, Half>(ref value)),
                    PType.F32 => FilterLiteral.From((double)Unsafe.As<TValue, float>(ref value)),
                    _ => FilterLiteral.From(Unsafe.As<TValue, double>(ref value)),
                };
        }
    }
}
