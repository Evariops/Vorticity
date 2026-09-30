using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>A column every row of which is null.</summary>
internal sealed class NullStore : ColumnStore
{
    internal NullStore(DType dtype, AlignedBufferPool pool)
        : base(dtype, pool)
    {
    }

    internal override long CommittedBytes => 0;

    internal override int Build(CanonicalArena arena, int rows) => arena.AddNull(DType, rows);

    internal override void Discard(int rows)
    {
        Count -= rows;
        Committed = Math.Max(0, Committed - rows);
    }

    internal override void Truncate(int rows) => Count = rows;

    internal override void AppendDefault() => Count++;

    internal override void AppendNull() => Count++;
}

/// <summary>
/// Fixed-width values: a primitive, a decimal, or the storage of a date, a time, a timestamp or a
/// registered extension, whose conversion parameters it carries.
/// </summary>
internal sealed unsafe class FixedStore : ColumnStore
{
    private NativeSegmentOwner? _owner;
    private byte* _data;
    private int _capacityBytes;
    private int _capacity;

    internal FixedStore(DType dtype, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        if (dtype.Kind == DTypeKind.Decimal)
        {
            IsDecimal = true;
            Precision = dtype.Precision;
            Scale = dtype.Scale;
            Storage = DecimalStorage.ForPrecision(dtype.Precision);
            Width = DecimalStorage.ByteWidth(Storage);
        }
        else
        {
            PType = dtype.PType;
            Width = PType.ByteWidth();
        }
    }

    /// <summary>Bytes per value.</summary>
    internal int Width { get; }

    /// <summary>The physical type of a primitive.</summary>
    internal PType PType { get; }

    internal bool IsDecimal { get; }

    internal DecimalStorageType Storage { get; }

    internal byte Precision { get; }

    internal sbyte Scale { get; }

    /// <summary>The unit of the date, time or timestamp this store holds.</summary>
    internal TimeUnit TemporalUnit { get; set; }

    /// <summary>Whether the timestamp column is UTC, so a local <see cref="DateTime"/> is converted before it is stored.</summary>
    internal bool UtcTimestamp { get; set; }

    internal override long CommittedBytes => ((long)Committed * Width) + ValidityBytes(Committed);

    /// <summary>A span of exactly <paramref name="sizeHint"/> values past the last, or of every value the buffer holds when it is 0.</summary>
    internal Span<T> GetSpan<T>(int sizeHint)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int wanted = Math.Max(sizeHint, 1);
        if (_capacity - Count < wanted)
        {
            GrowTo((long)Count + wanted);
        }

        return new Span<T>(_data + ((long)Count * Width), sizeHint > 0 ? sizeHint : _capacity - Count);
    }

    /// <summary>The <paramref name="count"/> values written past the last since the last span, not yet committed.</summary>
    internal ReadOnlySpan<T> Pending<T>(int count)
        where T : unmanaged
    {
        if ((uint)count > (uint)(_capacity - Count))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"The last span holds {_capacity - Count} values.");
        }

        return new ReadOnlySpan<T>(_data + ((long)Count * Width), count);
    }

    internal void Advance(int count)
    {
        if ((uint)count > (uint)(_capacity - Count))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"The last span holds {_capacity - Count} values.");
        }

        Count += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append<T>(T value)
        where T : unmanaged
    {
        if (Count == _capacity)
        {
            GrowTo((long)Count + 1);
        }

        ((T*)_data)[Count++] = value;
    }

    internal void Append<T>(ReadOnlySpan<T> values)
        where T : unmanaged
    {
        if (_capacity - Count < values.Length)
        {
            GrowTo((long)Count + values.Length);
        }

        values.CopyTo(new Span<T>(_data + ((long)Count * Width), values.Length));
        Count += values.Length;
    }

    internal void AppendBytes(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / Width;
        if (_capacity - Count < count)
        {
            GrowTo((long)Count + count);
        }

        bytes.CopyTo(new Span<byte>(_data + ((long)Count * Width), bytes.Length));
        Count += count;
    }

    /// <summary>The bytes of <paramref name="count"/> values past the last, which the caller fills; the values count as appended.</summary>
    internal Span<byte> Reserve(int count)
    {
        if (_capacity - Count < count)
        {
            GrowTo((long)Count + count);
        }

        Span<byte> slot = new Span<byte>(_data + ((long)Count * Width), count * Width);
        Count += count;
        return slot;
    }

    internal void Append<T>(ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity)
        where T : unmanaged
    {
        int row = Count;
        Append(values);
        CopyValidity(row, validity, values.Length);
    }

    internal override void AppendNull() => AppendNulls(1);

    internal void AppendNulls(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RequireNullable();
        int row = Count;
        Reserve(count).Clear();
        MarkNulls(row, count);
    }

    internal override void AppendDefault() => Reserve(1).Clear();

    internal override int Build(CanonicalArena arena, int rows)
    {
        Validity validity = BuildValidity(arena, rows);
        VortexBuffer values = View(_owner, rows * Width);
        return IsDecimal
            ? arena.AddDecimal(DType, rows, validity, Storage, Precision, Scale, values)
            : arena.AddPrimitive(DType, rows, validity, PType, values);
    }

    internal override void Discard(int rows)
    {
        int left = Count - rows;
        if (left > 0)
        {
            Buffer.MemoryCopy(_data + ((long)rows * Width), _data, _capacityBytes, (long)left * Width);
        }

        Count = left;
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        Count = rows;
        TruncateValidity(rows);
    }

    internal override void Release()
    {
        if (_owner is not null)
        {
            Pool.Return(_owner);
            _owner = null;
        }

        _data = null;
        _capacityBytes = 0;
        _capacity = 0;
        base.Release();
    }

    private void GrowTo(long values)
    {
        Grow(Pool, ref _owner, ref _data, ref _capacityBytes, (long)Count * Width, values * Width);
        _capacity = _capacityBytes / Width;
    }
}

/// <summary>Booleans, one bit per row, least significant first.</summary>
internal sealed unsafe class BoolStore : ColumnStore
{
    private NativeSegmentOwner? _owner;
    private byte* _bits;
    private int _capacityBytes;

    internal BoolStore(DType dtype, AlignedBufferPool pool)
        : base(dtype, pool)
    {
    }

    internal override long CommittedBytes => ((Committed + 7L) >> 3) + ValidityBytes(Committed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(bool value)
    {
        if (Count >= _capacityBytes << 3)
        {
            GrowTo(Count + 1);
        }

        int bit = Count++;
        byte mask = (byte)(1 << (bit & 7));
        ref byte at = ref _bits[bit >> 3];
        at = value ? (byte)(at | mask) : (byte)(at & ~mask);
    }

    /// <remarks>
    /// The values up to the next whole byte of the bitmap are appended one by one, and the rest
    /// packed a byte per value to a bit per value straight into it, sixty-four to a compare where
    /// there are 512-bit vectors, where each value read, masked and wrote back a byte.
    /// </remarks>
    internal void Append(ReadOnlySpan<bool> values)
    {
        GrowTo(Count + values.Length);
        int head = Math.Min((8 - (Count & 7)) & 7, values.Length);
        foreach (bool value in values[..head])
        {
            Append(value);
        }

        ReadOnlySpan<bool> rest = values[head..];
        if (rest.IsEmpty)
        {
            return;
        }

        BitmapKernels.PackBytes(
            MemoryMarshal.AsBytes(rest), new Span<byte>(_bits + (Count >> 3), (rest.Length + 7) >> 3));
        Count += rest.Length;
    }

    internal void AppendBits(ReadOnlySpan<ulong> bits, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)bits.Length * 64 < count)
        {
            throw new ArgumentException($"The words hold {bits.Length * 64L} bits for {count} values.", nameof(bits));
        }

        GrowTo(Count + count);
        BitmapKernels.CopyRange(MemoryMarshal.AsBytes(bits), 0, new Span<byte>(_bits, _capacityBytes), Count, count);
        Count += count;
    }

    internal override void AppendNull()
    {
        RequireNullable();
        int row = Count;
        Append(false);
        MarkNulls(row, 1);
    }

    internal override void AppendDefault() => Append(false);

    internal override int Build(CanonicalArena arena, int rows)
    {
        Validity validity = BuildValidity(arena, rows);
        if (rows == Count && (rows & 7) != 0)
        {
            _bits[rows >> 3] &= (byte)((1 << (rows & 7)) - 1);
        }

        return arena.AddBool(DType, rows, validity, View(_owner, (rows + 7) >> 3), 0);
    }

    internal override void Discard(int rows)
    {
        int left = Count - rows;
        if (left > 0)
        {
            Span<byte> bits = new Span<byte>(_bits, _capacityBytes);
            BitmapKernels.CopyRange(bits, rows, bits, 0, left);
        }

        Count = left;
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        Count = rows;
        TruncateValidity(rows);
    }

    internal override void Release()
    {
        if (_owner is not null)
        {
            Pool.Return(_owner);
            _owner = null;
        }

        _bits = null;
        _capacityBytes = 0;
        base.Release();
    }

    private void GrowTo(int bits)
    {
        long bytes = (bits + 7L) >> 3;
        if (bytes > _capacityBytes)
        {
            Grow(Pool, ref _owner, ref _bits, ref _capacityBytes, (Count + 7L) >> 3, bytes);
        }
    }
}

/// <summary>
/// Text or bytes as sixteen-byte views: a value of twelve bytes or fewer inline, a longer one in a
/// data buffer with its first four bytes in the view.
/// </summary>
internal sealed unsafe class VarBinStore : ColumnStore
{
    private const int ViewSize = CanonicalSupport.ViewSize;
    private const int MaxInline = CanonicalSupport.MaxInlineViewLength;

    private NativeSegmentOwner? _viewsOwner;
    private byte* _views;
    private int _viewBytes;
    private int _capacity;

    private NativeSegmentOwner? _dataOwner;
    private byte* _data;
    private int _dataCapacity;
    private int _dataLength;
    private int _dataCommitted;

    internal VarBinStore(DType dtype, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        IsUtf8 = dtype.Kind == DTypeKind.Utf8;
    }

    /// <summary>Whether the column is text, so the bytes written into it must be UTF-8.</summary>
    internal bool IsUtf8 { get; }

    internal override long CommittedBytes => ((long)Committed * ViewSize) + _dataCommitted + ValidityBytes(Committed);

    internal void Append(ReadOnlySpan<byte> value)
    {
        if (IsUtf8 && !Utf8.IsValid(value))
        {
            ThrowInvalid();
        }

        EnsureViews();
        int size = value.Length;
        ref byte view = ref _views[(long)Count * ViewSize];
        if (size <= MaxInline)
        {
            CanonicalSupport.WriteView(ref view, ref MemoryMarshal.GetReference(value), size, 0, 0);
        }
        else
        {
            EnsureData(size);
            byte* at = _data + _dataLength;
            value.CopyTo(new Span<byte>(at, size));
            CanonicalSupport.WriteView(ref view, ref *at, size, 0, _dataLength);
            _dataLength += size;
        }

        Count++;
    }

    internal void Append(ReadOnlySpan<char> text)
    {
        EnsureData((text.Length + 1) * 3);
        Span<byte> into = new Span<byte>(_data + _dataLength, _dataCapacity - _dataLength);
        Utf8.FromUtf16(text, into, out _, out int written);
        CommitInPlace(written);
    }

    internal void Append<TValue>(TValue value)
        where TValue : IUtf8SpanFormattable
    {
        int hint = 32;
        while (true)
        {
            EnsureData(hint);
            Span<byte> into = new Span<byte>(_data + _dataLength, _dataCapacity - _dataLength);
            if (value.TryFormat(into, out int written, default, CultureInfo.InvariantCulture))
            {
                CommitInPlace(written);
                return;
            }

            if (hint > 1 << 28)
            {
                throw new InvalidOperationException($"{typeof(TValue)} did not format into {into.Length} bytes.");
            }

            hint = Math.Max(hint, into.Length) * 2;
        }
    }

    /// <summary>A span of exactly <paramref name="sizeHint"/> bytes for one value, or of the whole free buffer when it is 0.</summary>
    internal Span<byte> GetSpan(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        EnsureData(Math.Max(sizeHint, 1));
        int free = _dataCapacity - _dataLength;
        return new Span<byte>(_data + _dataLength, sizeHint > 0 ? sizeHint : free);
    }

    internal void Commit(int length)
    {
        if ((uint)length > (uint)(_dataCapacity - _dataLength))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, $"The last span holds {_dataCapacity - _dataLength} bytes.");
        }

        if (IsUtf8 && !Utf8.IsValid(new ReadOnlySpan<byte>(_data + _dataLength, length)))
        {
            ThrowInvalid();
        }

        CommitInPlace(length);
    }

    internal override void AppendNull() => AppendNulls(1);

    internal void AppendNulls(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RequireNullable();
        int row = Count;
        for (int i = 0; i < count; i++)
        {
            AppendDefault();
        }

        MarkNulls(row, count);
    }

    internal override void AppendDefault()
    {
        EnsureViews();
        new Span<byte>(_views + ((long)Count * ViewSize), ViewSize).Clear();
        Count++;
    }

    internal override int Build(CanonicalArena arena, int rows)
    {
        Validity validity = BuildValidity(arena, rows);
        VortexBuffer views = View(_viewsOwner, rows * ViewSize);
        int end = DataEnd(rows);
        if (end == 0)
        {
            return arena.AddVarBinView(DType, rows, validity, views, default);
        }

        Span<VortexBuffer> data = stackalloc VortexBuffer[1];
        data[0] = View(_dataOwner, end);
        return arena.AddVarBinView(DType, rows, validity, views, data);
    }

    internal override void Commit()
    {
        Committed = Count;
        _dataCommitted = _dataLength;
    }

    internal override void Discard(int rows)
    {
        int start = DataEnd(rows);
        int left = Count - rows;
        if (left > 0)
        {
            Buffer.MemoryCopy(_views + ((long)rows * ViewSize), _views, _viewBytes, (long)left * ViewSize);
        }

        if (start > 0)
        {
            int bytes = _dataLength - start;
            if (bytes > 0)
            {
                Buffer.MemoryCopy(_data + start, _data, _dataCapacity, bytes);
            }

            uint* words = (uint*)_views;
            for (int i = 0; i < left; i++)
            {
                if (words[i * 4] > MaxInline)
                {
                    words[(i * 4) + 3] -= (uint)start;
                }
            }

            _dataLength -= start;
            _dataCommitted = Math.Max(0, _dataCommitted - start);
        }

        Count = left;
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        _dataLength = DataEnd(rows);
        Count = rows;
        TruncateValidity(rows);
    }

    internal override void Release()
    {
        if (_viewsOwner is not null)
        {
            Pool.Return(_viewsOwner);
            _viewsOwner = null;
        }

        if (_dataOwner is not null)
        {
            Pool.Return(_dataOwner);
            _dataOwner = null;
        }

        _views = null;
        _data = null;
        _viewBytes = 0;
        _capacity = 0;
        _dataCapacity = 0;
        _dataLength = 0;
        _dataCommitted = 0;
        base.Release();
    }

    /// <summary>Writes the view of the value just written at the end of the data buffer.</summary>
    private void CommitInPlace(int length)
    {
        EnsureViews();
        byte* at = _data + _dataLength;
        ref byte view = ref _views[(long)Count * ViewSize];
        if (length <= MaxInline)
        {
            CanonicalSupport.WriteView(ref view, ref *at, length, 0, 0);
        }
        else
        {
            CanonicalSupport.WriteView(ref view, ref *at, length, 0, _dataLength);
            _dataLength += length;
        }

        Count++;
    }

    /// <summary>Where the data of the first <paramref name="rows"/> rows ends.</summary>
    private int DataEnd(int rows)
    {
        if (rows == Count)
        {
            return _dataLength;
        }

        if (rows == Committed)
        {
            return _dataCommitted;
        }

        uint* words = (uint*)_views;
        for (int i = rows - 1; i >= 0; i--)
        {
            uint size = words[i * 4];
            if (size > MaxInline)
            {
                return checked((int)(words[(i * 4) + 3] + size));
            }
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureViews()
    {
        if (Count == _capacity)
        {
            Grow(Pool, ref _viewsOwner, ref _views, ref _viewBytes, (long)Count * ViewSize, ((long)Count + 1) * ViewSize);
            _capacity = _viewBytes / ViewSize;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureData(int bytes)
    {
        if (_dataCapacity - _dataLength < bytes)
        {
            Grow(Pool, ref _dataOwner, ref _data, ref _dataCapacity, _dataLength, (long)_dataLength + bytes);
        }
    }

    [DoesNotReturn]
    private void ThrowInvalid() => throw new VortexSchemaException($"The column is {Type}: the bytes appended to it must be valid UTF-8.");
}
