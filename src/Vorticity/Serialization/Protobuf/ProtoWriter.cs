using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// A single-pass proto3 encoder over a pooled <see cref="byte"/> array.
/// </summary>
/// <remarks>
/// <para>
/// <b>proto3 implicit presence.</b> The plain field writers omit a field whose value equals the
/// proto3 default (0, false, empty), because that is what every conforming encoder does and what
/// a decoder assumes when the field is absent. The <c>...Always</c> variants emit unconditionally
/// and exist for fields with <em>explicit</em> presence — <c>optional</c> fields, and every field
/// inside a <c>oneof</c>, where "absent" and "present and zero" are different states. Both
/// behaviours occur in the vendored schemas, so both are part of the contract:
/// <c>Extension.metadata</c> is an <c>optional bytes</c> field, and every arm of
/// <c>ScalarValue.kind</c> is a <c>oneof</c> member.
/// </para>
/// <para>
/// <b>Ownership.</b> The backing array is rented from <see cref="ArrayPool{T}"/> and returned by
/// <see cref="Dispose"/>. <see cref="WrittenSpan"/> is only valid until the next mutation or
/// <see cref="Dispose"/>. A <c>default(ProtoWriter)</c> is usable: the array is rented lazily on
/// first write.
/// </para>
/// <para>
/// <b>This is a mutable struct, and the language cannot enforce that for us.</b> Pass it as
/// <c>ref ProtoWriter</c>, never by value — a copy writes into the same pooled array as the
/// original and the two <see cref="Length"/> counters then disagree. A <c>ref struct</c> would not
/// fix it: that forbids boxing and heap capture, not copying, and it is unavailable here anyway
/// because <see cref="MessageScope"/> holds a <c>ref ProtoWriter</c> and a ref field may not point
/// at a ref struct. <c>using var w = new ProtoWriter();</c> mutates in place (a <c>using</c> local is
/// read-only but is not defensively copied) and <see cref="BeginMessage"/> works on one, but C#
/// forbids passing a <c>using</c> variable as a <c>ref</c> argument (CS1657). A codec that hands
/// its writer to a <c>Write(ref ProtoWriter, …)</c> helper therefore needs a plain local disposed
/// in a <c>try</c>/<c>finally</c>.
/// </para>
/// </remarks>
internal struct ProtoWriter : IDisposable
{
    private const int DefaultCapacity = 256;
    private const int MinimumCapacity = 64;

    /// <summary>The bytes of the array's head, which holds where the body must stop.</summary>
    private const int Head = sizeof(int);

    /// <summary>The bytes of a <see cref="Widened"/> record.</summary>
    private const int RecordBytes = 5 * sizeof(int);

    /// <summary>
    /// The pooled array: a head holding where the body must stop, the body from the head on, and
    /// from where it must stop to the end, the messages closed inside the outermost open one whose
    /// length takes more than the byte reserved for it, the last closed first.
    /// </summary>
    /// <remarks>
    /// Those lengths live in the array rather than in the struct, so that a class holding a writer
    /// pays for the array's reference and two ints and no more; the head is read at a fixed place,
    /// as the array's length is.
    /// </remarks>
    private byte[]? _buffer;

    /// <summary>Where the next byte goes in the array, past its head; of no meaning with no array.</summary>
    private int _position;

    /// <summary>Number of <see cref="MessageScope"/> instances currently open, for LIFO checking.</summary>
    private int _openScopes;

    /// <summary>Creates a writer with an initial pooled capacity.</summary>
    /// <param name="initialCapacity">
    /// Capacity hint in bytes. Zero defers the rental to the first write.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public ProtoWriter(int initialCapacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = null;
        _position = 0;
        _openScopes = 0;
        if (initialCapacity > 0)
        {
            Open(ArrayPool<byte>.Shared.Rent(initialCapacity));
        }
    }

    /// <summary>
    /// The bytes written so far. Valid until the next mutation or <see cref="Dispose"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> WrittenSpan =>
        _buffer is null ? default : new ReadOnlySpan<byte>(_buffer, Head, _position - Head);

    /// <summary>Number of bytes written so far.</summary>
    public readonly int Length => _buffer is null ? 0 : _position - Head;

    /// <summary>Discards everything written, keeping the rented array for reuse.</summary>
    public void Clear()
    {
        _position = Head;

        // Lengths wait only while a message is open: with none open, the body may already run to the end.
        if (_openScopes != 0)
        {
            Abandon();
        }
    }

    /// <summary>Returns the pooled array. Safe to call more than once.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        _position = 0;
        _openScopes = 0;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Primitives
    // ---------------------------------------------------------------------------------------

    /// <summary>Writes a field tag: <c>(fieldNumber &lt;&lt; 3) | wireType</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fieldNumber"/> is outside 1 to 2^29 - 1, or <paramref name="wireType"/> is a
    /// group (3, 4) or an undefined value. Group framing is refused on write for the same reason
    /// the reader refuses it on read: proto3 never emits it.
    /// </exception>
    public void WriteTag(int fieldNumber, ProtoWireType wireType)
    {
        if ((uint)(fieldNumber - 1) >= (uint)ProtoWire.MaxFieldNumber)
        {
            ThrowFieldNumberOutOfRange(fieldNumber);
        }

        if (wireType is not (ProtoWireType.Varint or ProtoWireType.Fixed64
            or ProtoWireType.LengthDelimited or ProtoWireType.Fixed32))
        {
            ThrowWireTypeNotWritable(wireType);
        }

        WriteVarint(((ulong)(uint)fieldNumber << ProtoWire.TagTypeBits) | (byte)wireType);
    }

    /// <summary>Writes a base-128 varint.</summary>
    public void WriteVarint(ulong value)
    {
        // One capacity check covers the whole varint, so its bytes are written unchecked.
        EnsureCapacity(Varint.MaxLength64);
        int pos = _position;
        _position = pos + Varint.Write(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buffer!), pos), value);
    }

    /// <summary>Appends raw bytes with no tag and no length prefix.</summary>
    public void WriteRawBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureCapacity(bytes.Length);
        bytes.CopyTo(new Span<byte>(_buffer!, _position, bytes.Length));
        _position += bytes.Length;
    }

    // ---------------------------------------------------------------------------------------
    // proto3 field writers: implicit presence (omit the default)
    // ---------------------------------------------------------------------------------------

    /// <summary>Writes a <c>bool</c> field, omitted when false.</summary>
    public void WriteBool(int fieldNumber, bool value)
    {
        if (value)
        {
            WriteBoolAlways(fieldNumber, value);
        }
    }

    /// <summary>Writes an <c>int32</c> field, omitted when 0. Negative values take ten bytes.</summary>
    public void WriteInt32(int fieldNumber, int value)
    {
        if (value != 0)
        {
            WriteInt32Always(fieldNumber, value);
        }
    }

    /// <summary>Writes an <c>int64</c> field, omitted when 0. Negative values take ten bytes.</summary>
    public void WriteInt64(int fieldNumber, long value)
    {
        if (value != 0)
        {
            WriteInt64Always(fieldNumber, value);
        }
    }

    /// <summary>Writes a <c>uint32</c> field, omitted when 0.</summary>
    public void WriteUInt32(int fieldNumber, uint value)
    {
        if (value != 0)
        {
            WriteUInt32Always(fieldNumber, value);
        }
    }

    /// <summary>Writes a <c>uint64</c> field, omitted when 0.</summary>
    public void WriteUInt64(int fieldNumber, ulong value)
    {
        if (value != 0)
        {
            WriteUInt64Always(fieldNumber, value);
        }
    }

    /// <summary>Writes a ZigZag <c>sint64</c> field, omitted when 0.</summary>
    public void WriteSInt64(int fieldNumber, long value)
    {
        if (value != 0)
        {
            WriteSInt64Always(fieldNumber, value);
        }
    }

    /// <summary>Writes a ZigZag <c>sint32</c> field, omitted when 0.</summary>
    public void WriteSInt32(int fieldNumber, int value)
    {
        if (value != 0)
        {
            WriteSInt32Always(fieldNumber, value);
        }
    }

    /// <summary>Writes an enum field as an <c>int32</c> varint, omitted when 0.</summary>
    public void WriteEnum(int fieldNumber, int value)
    {
        if (value != 0)
        {
            WriteEnumAlways(fieldNumber, value);
        }
    }

    /// <summary>
    /// Writes a <c>float</c> field, omitted when it equals zero.
    /// </summary>
    /// <remarks>
    /// The test is <c>value != 0</c>, matching every conforming proto3 encoder — which means
    /// negative zero is omitted and reads back as <c>+0</c>. Use <see cref="WriteFloatAlways"/>
    /// when the sign of zero must survive, as it must for a <c>oneof</c> member such as
    /// <c>ScalarValue.f32_value</c>.
    /// </remarks>
    public void WriteFloat(int fieldNumber, float value)
    {
        if (value != 0)
        {
            WriteFloatAlways(fieldNumber, value);
        }
    }

    /// <summary>
    /// Writes a <c>double</c> field, omitted when it equals zero. See <see cref="WriteFloat"/>
    /// for the negative-zero caveat.
    /// </summary>
    public void WriteDouble(int fieldNumber, double value)
    {
        if (value != 0)
        {
            WriteDoubleAlways(fieldNumber, value);
        }
    }

    /// <summary>Writes a <c>bytes</c> field, omitted when empty.</summary>
    public void WriteBytes(int fieldNumber, ReadOnlySpan<byte> value)
    {
        if (!value.IsEmpty)
        {
            WriteBytesAlways(fieldNumber, value);
        }
    }

    /// <summary>Writes a <c>string</c> field from pre-encoded UTF-8, omitted when empty.</summary>
    public void WriteStringUtf8(int fieldNumber, ReadOnlySpan<byte> utf8)
    {
        if (!utf8.IsEmpty)
        {
            WriteBytesAlways(fieldNumber, utf8);
        }
    }

    /// <summary>
    /// Writes a <c>string</c> field, omitted when null or empty. Transcodes straight into the
    /// pooled buffer: no intermediate array.
    /// </summary>
    public void WriteString(int fieldNumber, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            WriteStringAlways(fieldNumber, value);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Explicit presence: always emitted, even at the default value
    // ---------------------------------------------------------------------------------------

    /// <summary>Writes a <c>bool</c> field even when false.</summary>
    public void WriteBoolAlways(int fieldNumber, bool value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(value ? 1UL : 0UL);
    }

    /// <summary>Writes a <c>uint32</c> field even when 0.</summary>
    public void WriteUInt32Always(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(value);
    }

    /// <summary>Writes a <c>uint64</c> field even when 0.</summary>
    public void WriteUInt64Always(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(value);
    }

    /// <summary>Writes an <c>int32</c> field even when 0, sign-extended to ten bytes when negative.</summary>
    public void WriteInt32Always(int fieldNumber, int value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(unchecked((ulong)(long)value));
    }

    /// <summary>Writes an <c>int64</c> field even when 0, sign-extended to ten bytes when negative.</summary>
    public void WriteInt64Always(int fieldNumber, long value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(unchecked((ulong)value));
    }

    /// <summary>Writes an enum field even when 0.</summary>
    public void WriteEnumAlways(int fieldNumber, int value) => WriteInt32Always(fieldNumber, value);

    /// <summary>Writes a ZigZag <c>sint32</c> field even when 0.</summary>
    public void WriteSInt32Always(int fieldNumber, int value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(Varint.ZigZagEncode32(value));
    }

    /// <summary>Writes a ZigZag <c>sint64</c> field even when 0.</summary>
    public void WriteSInt64Always(int fieldNumber, long value)
    {
        WriteTag(fieldNumber, ProtoWireType.Varint);
        WriteVarint(Varint.ZigZagEncode64(value));
    }

    /// <summary>Writes a <c>float</c> field even when 0, preserving negative zero and NaN payloads.</summary>
    public void WriteFloatAlways(int fieldNumber, float value)
    {
        WriteTag(fieldNumber, ProtoWireType.Fixed32);
        EnsureCapacity(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(
            new Span<byte>(_buffer!, _position, sizeof(uint)), BitConverter.SingleToUInt32Bits(value));
        _position += sizeof(uint);
    }

    /// <summary>Writes a <c>double</c> field even when 0, preserving negative zero and NaN payloads.</summary>
    public void WriteDoubleAlways(int fieldNumber, double value)
    {
        WriteTag(fieldNumber, ProtoWireType.Fixed64);
        EnsureCapacity(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(_buffer!, _position, sizeof(ulong)), BitConverter.DoubleToUInt64Bits(value));
        _position += sizeof(ulong);
    }

    /// <summary>Writes a <c>fixed64</c> field even when 0.</summary>
    public void WriteFixed64Always(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, ProtoWireType.Fixed64);
        EnsureCapacity(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(_buffer!, _position, sizeof(ulong)), value);
        _position += sizeof(ulong);
    }

    /// <summary>Writes a <c>bytes</c> field even when empty.</summary>
    public void WriteBytesAlways(int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
        WriteVarint((uint)value.Length);
        WriteRawBytes(value);
    }

    /// <summary>Writes a <c>string</c> field from pre-encoded UTF-8 even when empty.</summary>
    public void WriteStringUtf8Always(int fieldNumber, ReadOnlySpan<byte> utf8) =>
        WriteBytesAlways(fieldNumber, utf8);

    /// <summary>Writes a <c>string</c> field even when null or empty.</summary>
    public void WriteStringAlways(int fieldNumber, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteBytesAlways(fieldNumber, default);
            return;
        }

        // GetByteCount and GetBytes apply the same replacement fallback to unpaired surrogates,
        // so the length prefix always matches the bytes that follow.
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
        WriteVarint((uint)byteCount);
        EnsureCapacity(byteCount);
        int written = Encoding.UTF8.GetBytes(value.AsSpan(), new Span<byte>(_buffer!, _position, byteCount));
        _position += written;
    }

    // ---------------------------------------------------------------------------------------
    // Nested messages
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Begins a nested length-delimited message under <paramref name="fieldNumber"/>. Write the
    /// body through this same writer, then <see cref="MessageScope.Dispose"/> (or
    /// <see cref="MessageScope.End"/>) to backpatch the length. Scopes nest and must be closed in
    /// last-opened-first-closed order.
    /// </summary>
    /// <remarks>
    /// One byte is reserved for the length varint before the body is written. A message whose
    /// length fits it writes it at its close. A longer one only notes its length, which counts the
    /// bytes the longer lengths inside it take past their reserved one, and the outermost
    /// message's close then puts every such length in its slot and every body after it, moving
    /// each byte once, from the end: a body is not shifted again at each enclosing close, which
    /// made a deep message cost its bytes times its depth. A message that holds a longer one is
    /// longer itself, so a length written at a close never moves apart from its body.
    /// </remarks>
    [UnscopedRef]
    public MessageScope BeginMessage(int fieldNumber)
    {
        WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
        EnsureCapacity(1);
        int lengthPosition = _position;
        _buffer![lengthPosition] = 0;
        _position = lengthPosition + 1;
        _openScopes++;
        return new MessageScope(ref this, lengthPosition, _openScopes);
    }

    private void EndMessage(int lengthPosition, int depth)
    {
        if (depth != _openScopes)
        {
            ThrowScopeOutOfOrder(depth, _openScopes);
        }

        int bodyLength = _position - (lengthPosition + 1);
        _openScopes = depth - 1;

        // A body under 128 bytes holds no longer length: the innermost message with one has a
        // body of 128 bytes at least, and every message around it holds that body whole.
        if ((uint)bodyLength < 0x80)
        {
            _buffer![lengthPosition] = (byte)bodyLength;
            return;
        }

        Widen(lengthPosition, bodyLength);
    }

    /// <summary>Notes a closed message whose length takes more than its reserved byte, and places them all when it is the outermost.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Widen(int lengthPosition, int bodyLength)
    {
        try
        {
            if (bodyLength < 0)
            {
                ThrowScopeCorrupted();
            }

            // The longer lengths it holds are the ones noted since it opened, whose slots are past
            // its own: they closed inside it, last, and the array holds them first. Each record
            // counts its own subtree, so only the outermost of them are visited, from the last closed.
            Span<Widened> waiting = Waiting();
            int inside = 0;
            int count = 1;
            for (int i = 0; i < waiting.Length && waiting[i].Slot > lengthPosition; i += waiting[i].Count)
            {
                inside += waiting[i].Below;
                count += waiting[i].Count;
            }

            // The body as it will be once placed: what it holds, and the bytes the longer lengths
            // inside it take past their reserved one.
            long length = (long)bodyLength + inside;
            if (length > Array.MaxLength)
            {
                ThrowTooLarge(length);
            }

            int extra = Varint.Size((ulong)length) - 1;
            byte[] buffer = _buffer!;
            if (Limit(buffer) - _position < RecordBytes)
            {
                Grow(RecordBytes);
                buffer = _buffer!;
            }

            // The body must stop a record sooner, and the record takes the room.
            int limit = Limit(buffer) - RecordBytes;
            Widened record = new Widened(lengthPosition, (int)length, extra, inside + extra, count);
            MemoryMarshal.Write(buffer.AsSpan(limit, RecordBytes), in record);
            Limit(buffer) = limit;
            if (_openScopes == 0)
            {
                Place(inside + extra);
            }
        }
        finally
        {
            // Once the outermost message is closed, its lengths placed or given up, none waits,
            // which lets a clear with no message open leave the end of the array alone.
            if (_openScopes == 0 && _buffer is { } buffer)
            {
                Limit(buffer) = End(buffer);
            }
        }
    }

    /// <summary>
    /// Puts every longer length in its slot and every byte after a slot past the extra bytes of
    /// the longer lengths before it, from the end: each byte moves once.
    /// </summary>
    private void Place(int extraBytes)
    {
        // The body grows into the room before the records, which move with the array if it grows.
        EnsureCapacity(extraBytes);
        byte[] buffer = _buffer!;
        Span<Widened> waiting = Waiting();

        // The pass wants them from the last slot, and takes them from the end of the array, which
        // holds them the last closed first. They closed innermost first, which puts them in slot
        // order already for messages nested one in the other; siblings are sorted.
        for (int i = 1; i < waiting.Length; i++)
        {
            if (waiting[i].Slot < waiting[i - 1].Slot)
            {
                waiting.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
                break;
            }
        }

        int end = _position;
        int shift = extraBytes;
        for (int i = waiting.Length - 1; i >= 0; i--)
        {
            (int slot, int length, int extra, _, _) = waiting[i];

            // Span.CopyTo is a memmove, so the overlapping move is well defined.
            new Span<byte>(buffer, slot + 1, end - slot - 1).CopyTo(new Span<byte>(buffer, slot + 1 + shift, end - slot - 1));
            shift -= extra;
            ulong remaining = (uint)length;
            int pos = slot + shift;
            while (remaining >= 0x80)
            {
                buffer[pos++] = (byte)(remaining | 0x80);
                remaining >>= 7;
            }

            buffer[pos] = (byte)remaining;
            end = slot;
        }

        _position += extraBytes;
    }

    /// <summary>The records waiting at the end of the array, the last closed first.</summary>
    private readonly Span<Widened> Waiting()
    {
        byte[] buffer = _buffer!;
        int limit = Limit(buffer);
        return MemoryMarshal.Cast<byte, Widened>(buffer.AsSpan(limit, End(buffer) - limit));
    }

    /// <summary>
    /// A closed message whose length takes more than its reserved byte: its slot, its length, the
    /// bytes past the reserved one, the same bytes with those of every longer length inside it,
    /// and the records of its subtree, itself included, which the array holds from it on.
    /// </summary>
    private readonly record struct Widened(int Slot, int Length, int Extra, int Below, int Count);

    /// <summary>
    /// The open-nested-message token returned by <see cref="BeginMessage"/>. Holds a
    /// <c>ref</c> to the writer, so it must not outlive it.
    /// </summary>
    public ref struct MessageScope
    {
        private ref ProtoWriter _writer;
        private readonly int _lengthPosition;
        private int _depth;

        internal MessageScope(ref ProtoWriter writer, int lengthPosition, int depth)
        {
            _writer = ref writer;
            _lengthPosition = lengthPosition;
            _depth = depth;
        }

        /// <summary>
        /// Closes the nested message; its length varint is in place once the outermost open
        /// message is closed too. Idempotent; a <c>default(MessageScope)</c> does nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Scopes were closed out of order (an enclosing scope closed before one it contains).
        /// </exception>
        public void End()
        {
            // depth 0 means "never opened" (default instance) or "already closed".
            int depth = _depth;
            if (depth == 0)
            {
                return;
            }

            _depth = 0;
            _writer.EndMessage(_lengthPosition, depth);
        }

        /// <summary>Equivalent to <see cref="End"/>.</summary>
        public void Dispose() => End();
    }

    // ---------------------------------------------------------------------------------------
    // Capacity
    // ---------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int additional)
    {
        byte[]? buffer = _buffer;
        if (buffer is not null)
        {
            // Read before the position, as the array's length would be, so that the compare does
            // not wait on it. The uint cast makes an int overflow of `_position + additional` fold
            // into the "too large" branch instead of wrapping to a value that passes the test.
            int limit = Limit(buffer);
            if ((uint)(_position + additional) <= (uint)limit)
            {
                return;
            }
        }

        Grow(additional);
    }

    /// <summary>Moves to an array with room for <paramref name="additional"/> more bytes of body, the records kept at its end.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additional)
    {
        byte[]? previous = _buffer;
        int start = previous is null ? Head : _position;
        int records = previous is null ? 0 : End(previous) - Limit(previous);
        long required = (long)start + additional + records;
        if (required > (Array.MaxLength & -sizeof(int)))
        {
            ThrowTooLarge(required);
        }

        int size = previous is null ? DefaultCapacity : Math.Max(End(previous), MinimumCapacity);
        while (size < required)
        {
            size = size >= Array.MaxLength / 2 ? Array.MaxLength : size * 2;
        }

        byte[] next = ArrayPool<byte>.Shared.Rent(size);
        if (previous is null)
        {
            Open(next);
            return;
        }

        // The limit is set before the copies, so that it is long written when the next check reads it.
        int limit = End(next) - records;
        Limit(next) = limit;
        new ReadOnlySpan<byte>(previous, Head, _position - Head).CopyTo(new Span<byte>(next, Head, _position - Head));
        if (records != 0)
        {
            new ReadOnlySpan<byte>(previous, End(previous) - records, records).CopyTo(new Span<byte>(next, limit, records));
        }

        ArrayPool<byte>.Shared.Return(previous);
        _buffer = next;
    }

    /// <summary>Drops the open messages a clear left, and the lengths they had waiting.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Abandon()
    {
        _openScopes = 0;
        if (_buffer is { } buffer)
        {
            Limit(buffer) = End(buffer);
        }
    }

    /// <summary>Starts on <paramref name="buffer"/> with nothing written and no length waiting.</summary>
    private void Open(byte[] buffer)
    {
        _buffer = buffer;
        _position = Head;
        Limit(buffer) = End(buffer);
    }

    /// <summary>Where the records of <paramref name="buffer"/> end: its length taken down to a whole int.</summary>
    private static int End(byte[] buffer) => buffer.Length & -sizeof(int);

    /// <summary>Where the body must stop in <paramref name="buffer"/>, the writer's array, which its head holds.</summary>
    /// <remarks>Read without a bounds check: a rented array is never shorter than its head.</remarks>
    private static ref int Limit(byte[] buffer) =>
        ref Unsafe.As<byte, int>(ref MemoryMarshal.GetArrayDataReference(buffer));

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFieldNumberOutOfRange(int fieldNumber) =>
        throw new ArgumentOutOfRangeException(
            nameof(fieldNumber), fieldNumber,
            $"A Protobuf field number must be between 1 and {ProtoWire.MaxFieldNumber}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWireTypeNotWritable(ProtoWireType wireType) =>
        throw new ArgumentOutOfRangeException(
            nameof(wireType), wireType,
            "Only the varint, fixed64, length-delimited and fixed32 wire types can be written; " +
            "groups (3, 4) are a proto2 framing that proto3 never emits.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowScopeOutOfOrder(int depth, int openScopes) =>
        throw new InvalidOperationException(
            $"ProtoWriter nested-message scopes must be closed in reverse order of opening: " +
            $"tried to close depth {depth} while depth {openScopes} is open.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowScopeCorrupted() =>
        throw new InvalidOperationException(
            "ProtoWriter nested-message scope is invalid: the writer was cleared or rewound while " +
            "the scope was open.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLarge(long required) =>
        throw new InvalidOperationException(
            $"ProtoWriter needs {required} bytes, which exceeds the maximum array length of " +
            $"{Array.MaxLength}.");
}
