using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization;

namespace Vorticity.Parquet.Thrift;

/// <summary>
/// Reads the Thrift compact protocol in place: integers decoded from their varints, binaries and
/// strings handed out as slices of the input, unknown fields skipped by their wire type.
/// </summary>
/// <remarks>
/// <para>
/// Every read is bounded by the input. A varint longer than its type allows, a length past the end,
/// a list declaring more elements than there are bytes left, or nesting deeper than
/// <see cref="MaxDepth"/> throws <see cref="ParquetFormatException"/>. No element of a list takes
/// less than one byte, so a list's size is never larger than the input and a caller may size an
/// array by it.
/// </para>
/// <para>
/// A struct is read between <see cref="EnterStruct"/> and <see cref="ExitStruct"/>: a field header
/// carries its id as a delta from the previous field of the same struct, which the reader keeps.
/// </para>
/// </remarks>
internal ref struct ThriftCompactReader
{
    /// <summary>How deep structs, lists and maps may nest, the read structures and the skipped ones together.</summary>
    internal const int MaxDepth = 64;

    private readonly ReadOnlySpan<byte> _input;
    private int _position;
    private short _lastFieldId;
    private int _depth;

    /// <summary>A reader over <paramref name="input"/>, positioned at its first byte.</summary>
    internal ThriftCompactReader(ReadOnlySpan<byte> input)
    {
        _input = input;
    }

    /// <summary>The bytes read so far.</summary>
    internal readonly int Position => _position;

    /// <summary>The bytes left.</summary>
    internal readonly int Remaining => _input.Length - _position;

    /// <summary>Enters a struct, returning the enclosing struct's last field id for <see cref="ExitStruct"/>.</summary>
    internal short EnterStruct()
    {
        if (++_depth > MaxDepth)
        {
            ThrowTooDeep();
        }

        short saved = _lastFieldId;
        _lastFieldId = 0;
        return saved;
    }

    /// <summary>Leaves a struct whose stop byte was read, restoring the enclosing struct's field ids.</summary>
    internal void ExitStruct(short saved)
    {
        _depth--;
        _lastFieldId = saved;
    }

    /// <summary>Reads a field header; false at the struct's stop byte.</summary>
    internal bool ReadFieldHeader(out ThriftType type, out short id)
    {
        byte header = ReadRawByte();
        if (header == 0)
        {
            type = ThriftType.Stop;
            id = 0;
            return false;
        }

        type = (ThriftType)(header & 0x0F);
        if (type is ThriftType.Stop or > ThriftType.Uuid)
        {
            ThrowFieldType(header);
        }

        int delta = header >> 4;
        if (delta != 0)
        {
            int next = _lastFieldId + delta;
            if (next > short.MaxValue)
            {
                ParquetThrow.Format("A Thrift field id passes 32767.");
            }

            id = (short)next;
        }
        else
        {
            id = ReadI16();
        }

        _lastFieldId = id;
        return true;
    }

    /// <summary>The value of a boolean field, which its wire type carries.</summary>
    internal static bool BooleanField(ThriftType type)
    {
        if (type == ThriftType.BooleanTrue)
        {
            return true;
        }

        if (type != ThriftType.BooleanFalse)
        {
            ThrowWireType(type, ThriftType.BooleanTrue);
        }

        return false;
    }

    /// <summary>Reads a boolean element of a list: 1 is true, 2 and 0 are false.</summary>
    internal bool ReadBooleanElement()
    {
        byte value = ReadRawByte();
        if (value == 1)
        {
            return true;
        }

        if (value is not (0 or 2))
        {
            ParquetThrow.Format("A Thrift boolean element is neither 1 nor 2.");
        }

        return false;
    }

    /// <summary>Reads an <c>i8</c>.</summary>
    internal sbyte ReadByte() => (sbyte)ReadRawByte();

    /// <summary>Reads an <c>i16</c>.</summary>
    internal short ReadI16()
    {
        int value = Varint.ZigZagDecode32(ReadVarint32());
        if (value is < short.MinValue or > short.MaxValue)
        {
            ParquetThrow.Format("A Thrift i16 is out of its range.");
        }

        return (short)value;
    }

    /// <summary>Reads an <c>i32</c> or an enum.</summary>
    internal int ReadI32() => Varint.ZigZagDecode32(ReadVarint32());

    /// <summary>Reads an <c>i64</c>.</summary>
    internal long ReadI64() => Varint.ZigZagDecode64(ReadVarint64());

    /// <summary>Reads a <c>double</c>.</summary>
    internal double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Reads a <c>binary</c> or a <c>string</c>, as a slice of the input.</summary>
    internal ReadOnlySpan<byte> ReadBinary()
    {
        uint length = ReadVarint32();
        if (length > (uint)Remaining)
        {
            ParquetThrow.Truncated("Thrift binary");
        }

        return Take((int)length);
    }

    /// <summary>Reads a list or set header: the element type and the count, which the bytes left bound.</summary>
    internal int ReadListHeader(out ThriftType elementType)
    {
        byte header = ReadRawByte();
        elementType = (ThriftType)(header & 0x0F);
        if (elementType is ThriftType.Stop or > ThriftType.Uuid)
        {
            ParquetThrow.Format("A Thrift list declares an unknown element type.");
        }

        uint count = (uint)(header >> 4);
        if (count == 15)
        {
            count = ReadVarint32();
        }

        if (count > (uint)Remaining)
        {
            ParquetThrow.Format("A Thrift list declares more elements than bytes remain.");
        }

        return (int)count;
    }

    /// <summary>
    /// Checks that a field's wire type is <paramref name="expected"/>; a list's element type is
    /// checked against its own, a boolean list accepting either boolean type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Expect(ThriftType actual, ThriftType expected)
    {
        if (actual != expected)
        {
            ThrowWireType(actual, expected);
        }
    }

    /// <summary>Checks that a list holds booleans, which either boolean type declares.</summary>
    internal static void ExpectBooleanElements(ThriftType elementType)
    {
        if (elementType is not (ThriftType.BooleanTrue or ThriftType.BooleanFalse))
        {
            ThrowWireType(elementType, ThriftType.BooleanTrue);
        }
    }

    /// <summary>Skips a field's value: a boolean field has none, a container is walked to its end.</summary>
    internal void Skip(ThriftType type)
    {
        switch (type)
        {
            case ThriftType.BooleanTrue:
            case ThriftType.BooleanFalse:
                return;
            case ThriftType.Struct:
            case ThriftType.List:
            case ThriftType.Set:
            case ThriftType.Map:
                SkipContainer(type);
                return;
            default:
                SkipScalar(type);
                return;
        }
    }

    private void SkipScalar(ThriftType type)
    {
        switch (type)
        {
            case ThriftType.Byte:
                Take(1);
                return;
            case ThriftType.I16:
            case ThriftType.I32:
                ReadVarint32();
                return;
            case ThriftType.I64:
                ReadVarint64();
                return;
            case ThriftType.Double:
                Take(8);
                return;
            case ThriftType.Binary:
                ReadBinary();
                return;
            case ThriftType.Uuid:
                Take(16);
                return;
            default:
                ThrowFieldType((byte)type);
                return;
        }
    }

    /// <summary>
    /// Walks a struct, list, set or map to its end with an explicit stack rather than recursion: the
    /// depth counts against <see cref="MaxDepth"/> with the structs being read, and every step
    /// consumes a byte, so the work is bounded by the input.
    /// </summary>
    private void SkipContainer(ThriftType type)
    {
        Span<SkipFrame> stack = stackalloc SkipFrame[MaxDepth];
        int top = -1;
        Push(stack, ref top, type);
        while (top >= 0)
        {
            ref SkipFrame frame = ref stack[top];
            ThriftType next;
            if (frame.Kind == ThriftType.Struct)
            {
                if (!ReadFieldHeader(out next, out _))
                {
                    _lastFieldId = frame.SavedFieldId;
                    _depth--;
                    top--;
                    continue;
                }

                if (next is ThriftType.BooleanTrue or ThriftType.BooleanFalse)
                {
                    continue;
                }
            }
            else
            {
                if (frame.Remaining == 0)
                {
                    _depth--;
                    top--;
                    continue;
                }

                frame.Remaining--;
                next = frame.Kind == ThriftType.Map && (frame.Remaining & 1) == 0 ? frame.ValueType : frame.ElementType;
                if (next is ThriftType.BooleanTrue or ThriftType.BooleanFalse)
                {
                    ReadBooleanElement();
                    continue;
                }
            }

            if (next is ThriftType.Struct or ThriftType.List or ThriftType.Set or ThriftType.Map)
            {
                Push(stack, ref top, next);
            }
            else
            {
                SkipScalar(next);
            }
        }
    }

    private void Push(scoped Span<SkipFrame> stack, ref int top, ThriftType type)
    {
        if (++_depth > MaxDepth)
        {
            ThrowTooDeep();
        }

        ref SkipFrame frame = ref stack[++top];
        frame = default;
        frame.Kind = type;
        switch (type)
        {
            case ThriftType.Struct:
                frame.SavedFieldId = _lastFieldId;
                _lastFieldId = 0;
                break;
            case ThriftType.List:
            case ThriftType.Set:
                frame.Kind = ThriftType.List;
                frame.Remaining = ReadListHeader(out frame.ElementType);
                break;
            default:
                // A map: its size, then, when it holds anything, the key and value types in one
                // byte. Keys and values alternate, the key first, so the frame counts both.
                uint size = ReadVarint32();
                if (size != 0)
                {
                    byte types = ReadRawByte();
                    frame.ElementType = (ThriftType)(types >> 4);
                    frame.ValueType = (ThriftType)(types & 0x0F);
                    if (frame.ElementType is ThriftType.Stop or > ThriftType.Uuid || frame.ValueType is ThriftType.Stop or > ThriftType.Uuid)
                    {
                        ParquetThrow.Format("A Thrift map declares an unknown key or value type.");
                    }
                }

                if ((ulong)size * 2 > (ulong)Remaining)
                {
                    ParquetThrow.Format("A Thrift map declares more entries than bytes remain.");
                }

                frame.Remaining = (int)(size * 2);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadRawByte()
    {
        int position = _position;
        ReadOnlySpan<byte> input = _input;
        if ((uint)position >= (uint)input.Length)
        {
            return ParquetThrow.Truncated<byte>("Thrift structure");
        }

        _position = position + 1;
        return input[position];
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        int position = _position;
        if ((uint)count > (uint)(_input.Length - position))
        {
            ParquetThrow.Truncated("Thrift structure");
        }

        _position = position + count;
        return _input.Slice(position, count);
    }

    private uint ReadVarint32() => Varint.Read32<VarintErrors>(_input, ref _position);

    private ulong ReadVarint64() => Varint.Read64<VarintErrors>(_input, ref _position);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooDeep() =>
        throw new ParquetFormatException($"Thrift structures nest deeper than {MaxDepth} levels.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFieldType(byte header) =>
        throw new ParquetFormatException($"A Thrift field header declares the unknown type {header & 0x0F}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWireType(ThriftType actual, ThriftType expected) =>
        throw new ParquetFormatException($"A Thrift field has wire type {actual} where {expected} is expected.");

    private struct SkipFrame
    {
        internal ThriftType Kind;
        internal ThriftType ElementType;
        internal ThriftType ValueType;
        internal short SavedFieldId;
        internal int Remaining;
    }

    /// <summary>The compact protocol's refusals of a varint.</summary>
    private readonly struct VarintErrors : IVarintErrors
    {
        public static ulong Truncated(int position) => ParquetThrow.Format<ulong>("A Thrift varint runs past its structure.");

        public static ulong Malformed(int position, byte value) => ParquetThrow.Format<ulong>("A Thrift varint holds more bits than its type.");
    }
}
