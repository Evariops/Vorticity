// The alignment rule for reinterpreted struct vectors is a property of the ADDRESS the elements
// land on, so a test for it must control that address. A managed byte[] does not: its base address
// is whatever the GC chose and can change under compaction. These tests therefore copy the byte
// vector into a 64-byte aligned native allocation, which makes the check deterministic and matches
// what the real reader sees, whose buffers are 64-byte aligned.
using System;
using System.Runtime.InteropServices;

namespace Vorticity.Tests.Serialization.FlatBuffers;

internal unsafe struct AlignedTestBuffer : IDisposable
{
    private byte* _pointer;
    private int _length;

    internal static AlignedTestBuffer Copy(ReadOnlySpan<byte> bytes)
    {
        byte* pointer = (byte*)NativeMemory.AlignedAlloc((nuint)bytes.Length, 64);
        bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
        return new AlignedTestBuffer { _pointer = pointer, _length = bytes.Length };
    }

    internal readonly ReadOnlySpan<byte> Span => new(_pointer, _length);

    public void Dispose()
    {
        if (_pointer != null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
            _length = 0;
        }
    }
}
