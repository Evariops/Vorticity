using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Serialization;

namespace Vorticity.Parquet.Thrift;

/// <summary>
/// Writes the Thrift compact protocol forward, into an <see cref="IBufferWriter{T}"/>: fields in the
/// order the caller gives them, in the short header form whenever the id's delta allows it.
/// </summary>
/// <remarks>
/// A struct is written between <see cref="BeginStruct"/> and <see cref="EndStruct"/>, which keep the
/// previous field id a header's delta is taken from. Nothing reaches the output before
/// <see cref="Flush"/>: the writer fills the span it was given and advances the output only when it
/// needs another, so a caller flushes once at the end.
/// </remarks>
internal ref struct ThriftCompactWriter
{
    private const int MinimumSpan = 256;

    private readonly IBufferWriter<byte> _output;
    private Span<byte> _span;
    private int _used;
    private short _lastFieldId;

    /// <summary>A writer appending to <paramref name="output"/>.</summary>
    internal ThriftCompactWriter(IBufferWriter<byte> output)
    {
        _output = output;
    }

    /// <summary>Hands what was written to the output.</summary>
    internal void Flush()
    {
        if (_used > 0)
        {
            _output.Advance(_used);
            _used = 0;
        }

        _span = default;
    }

    /// <summary>Begins a struct, top-level or a list's element, returning the enclosing struct's last field id.</summary>
    internal short BeginStruct()
    {
        short saved = _lastFieldId;
        _lastFieldId = 0;
        return saved;
    }

    /// <summary>Ends a struct with its stop byte, restoring the enclosing struct's field ids.</summary>
    internal void EndStruct(short saved)
    {
        WriteRawByte(0);
        _lastFieldId = saved;
    }

    /// <summary>Begins a struct field, returning the enclosing struct's last field id for <see cref="EndStruct"/>.</summary>
    internal short BeginStructField(short id)
    {
        WriteFieldHeader(ThriftType.Struct, id);
        return BeginStruct();
    }

    /// <summary>Writes a boolean field, whose value is its wire type.</summary>
    internal void WriteBooleanField(short id, bool value) =>
        WriteFieldHeader(value ? ThriftType.BooleanTrue : ThriftType.BooleanFalse, id);

    /// <summary>Writes an <c>i8</c> field.</summary>
    internal void WriteByteField(short id, sbyte value)
    {
        WriteFieldHeader(ThriftType.Byte, id);
        WriteRawByte((byte)value);
    }

    /// <summary>Writes an <c>i16</c> field.</summary>
    internal void WriteI16Field(short id, short value)
    {
        WriteFieldHeader(ThriftType.I16, id);
        WriteVarint32(Varint.ZigZagEncode32(value));
    }

    /// <summary>Writes an <c>i32</c> or an enum field.</summary>
    internal void WriteI32Field(short id, int value)
    {
        WriteFieldHeader(ThriftType.I32, id);
        WriteVarint32(Varint.ZigZagEncode32(value));
    }

    /// <summary>Writes an <c>i64</c> field.</summary>
    internal void WriteI64Field(short id, long value)
    {
        WriteFieldHeader(ThriftType.I64, id);
        WriteVarint64(Varint.ZigZagEncode64(value));
    }

    /// <summary>Writes a <c>double</c> field.</summary>
    internal void WriteDoubleField(short id, double value)
    {
        WriteFieldHeader(ThriftType.Double, id);
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        _used += 8;
    }

    /// <summary>Writes a <c>binary</c> field.</summary>
    internal void WriteBinaryField(short id, scoped ReadOnlySpan<byte> value)
    {
        WriteFieldHeader(ThriftType.Binary, id);
        WriteBinaryElement(value);
    }

    /// <summary>Writes a <c>string</c> field, encoded as UTF-8.</summary>
    internal void WriteStringField(short id, string value)
    {
        WriteFieldHeader(ThriftType.Binary, id);
        WriteStringElement(value);
    }

    /// <summary>Writes a list field's header; its elements follow.</summary>
    internal void WriteListField(short id, ThriftType elementType, int count)
    {
        WriteFieldHeader(ThriftType.List, id);
        WriteListHeader(elementType, count);
    }

    /// <summary>Writes a list header: the count in the header byte below 15, as a varint after it from 15.</summary>
    internal void WriteListHeader(ThriftType elementType, int count)
    {
        if (count < 15)
        {
            WriteRawByte((byte)((count << 4) | (int)elementType));
        }
        else
        {
            WriteRawByte((byte)(0xF0 | (int)elementType));
            WriteVarint32((uint)count);
        }
    }

    /// <summary>
    /// Writes a map header: the size as a varint, then, unless the map is empty, the key and value
    /// types in one byte. Parquet's structures hold no map; the method exists so that a skip over
    /// one can be tested.
    /// </summary>
    internal void WriteMapHeader(ThriftType keyType, ThriftType valueType, int count)
    {
        WriteVarint32((uint)count);
        if (count > 0)
        {
            WriteRawByte((byte)(((int)keyType << 4) | (int)valueType));
        }
    }

    /// <summary>Writes a boolean element of a list: 1 for true, 2 for false.</summary>
    internal void WriteBooleanElement(bool value) => WriteRawByte(value ? (byte)1 : (byte)2);

    /// <summary>Writes an <c>i32</c> element of a list.</summary>
    internal void WriteI32Element(int value) => WriteVarint32(Varint.ZigZagEncode32(value));

    /// <summary>Writes an <c>i64</c> element of a list.</summary>
    internal void WriteI64Element(long value) => WriteVarint64(Varint.ZigZagEncode64(value));

    /// <summary>Writes a <c>binary</c> element of a list.</summary>
    internal void WriteBinaryElement(scoped ReadOnlySpan<byte> value)
    {
        WriteVarint32((uint)value.Length);
        value.CopyTo(Reserve(value.Length));
        _used += value.Length;
    }

    /// <summary>Writes a <c>string</c> element of a list, encoded as UTF-8.</summary>
    internal void WriteStringElement(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        WriteVarint32((uint)length);
        Span<byte> destination = Reserve(length);
        _used += Encoding.UTF8.GetBytes(value, destination);
    }

    /// <summary>Writes a field header, in the short form when the id is 1 to 15 past the previous one.</summary>
    internal void WriteFieldHeader(ThriftType type, short id)
    {
        int delta = id - _lastFieldId;
        if (delta is > 0 and <= 15)
        {
            WriteRawByte((byte)((delta << 4) | (int)type));
        }
        else
        {
            WriteRawByte((byte)type);
            WriteVarint32(Varint.ZigZagEncode32(id));
        }

        _lastFieldId = id;
    }

    private Span<byte> Reserve(int count)
    {
        if (_span.Length - _used < count)
        {
            if (_used > 0)
            {
                _output.Advance(_used);
                _used = 0;
            }

            _span = _output.GetSpan(Math.Max(count, MinimumSpan));
        }

        return _span.Slice(_used);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRawByte(byte value)
    {
        Reserve(1)[0] = value;
        _used++;
    }

    // Reserve may hand the written bytes to the output and start a new span at zero, so the room
    // is taken before the count it advances is read.
    private void WriteVarint32(uint value)
    {
        Span<byte> destination = Reserve(Varint.MaxLength32);
        _used += Varint.Write(destination, value);
    }

    private void WriteVarint64(ulong value)
    {
        Span<byte> destination = Reserve(Varint.MaxLength64);
        _used += Varint.Write(destination, value);
    }
}
