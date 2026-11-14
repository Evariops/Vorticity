// Shared helpers for the schema-accessor tests.
//
// Two things need controlling that a managed byte[] does not give us:
//   * the ADDRESS a buffer lands on, because GetStructVector<T> checks the alignment of
//     the element address for any T whose natural alignment exceeds 1 (ushort, uint);
//   * the exact BYTES, because the builder always produces well-formed, well-aligned output and
//     several of the rules under test can only be violated by hand-assembling a buffer.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Vorticity.Tests.Serialization.Schemas;

/// <summary>A 64-byte aligned copy of a buffer in a pinned array, so alignment tests are deterministic.</summary>
internal struct AlignedBytes : IDisposable
{
    private Vorticity.Buffers.PinnedArraySegmentOwner? _owner;

    internal static AlignedBytes Copy(ReadOnlySpan<byte> bytes) =>
        new AlignedBytes { _owner = Vorticity.Buffers.PinnedArraySegmentOwner.CopyOf(bytes, VortexLimits.MaxAlignment) };

    internal readonly ReadOnlySpan<byte> Span => _owner is { } owner ? owner.Buffer.Span : default;

    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
    }
}

/// <summary>A forward byte assembler for hand-built FlatBuffers that no builder would emit.</summary>
internal sealed class RawBytes
{
    private byte[] _buffer = new byte[64];
    private int _length;

    internal int Position => _length;

    internal void U8(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    internal void U16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    internal void U32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    internal void I32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    internal void U64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length;
        while (capacity < _length + extra)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}

/// <summary>
/// Accumulates every value a reader returns into one number, so a truncation sweep can assert the
/// property that actually matters: a prefix either throws <c>VortexFormatException</c> or produces
/// exactly the values the untruncated buffer produced. "Every prefix throws" would be wrong -
/// FlatBuffers pads its tail, so the last few bytes of a buffer can be dropped losslessly.
/// </summary>
internal struct ValueDigest
{
    private ulong _state = 1469598103934665603UL;   // FNV-1a 64 offset basis

    public ValueDigest()
    {
    }

    internal void Add(ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            _state = (_state ^ (byte)(value >> (i * 8))) * 1099511628211UL;
        }
    }

    internal void Add(long value) => Add((ulong)value);

    internal void Add(bool value) => Add(value ? 1UL : 2UL);

    internal void Add(ReadOnlySpan<byte> bytes)
    {
        Add((ulong)bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            Add(bytes[i]);
        }
    }

    internal readonly long Value => (long)_state;
}
