// The alignment rule for reinterpreted struct vectors is a property of the ADDRESS the elements
// land on, so a test for it must control that address. A managed byte[] does not: its base address
// is whatever the GC chose and can change under compaction. These tests therefore copy the byte
// vector into a 64-byte aligned block of the library's own kind, a pinned array, which makes the
// check deterministic and matches what the real reader sees.
using System;
using Vorticity.Buffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

internal struct AlignedTestBuffer : IDisposable
{
    private BlockOwner? _owner;

    internal static AlignedTestBuffer Copy(ReadOnlySpan<byte> bytes) =>
        new AlignedTestBuffer { _owner = BlockOwner.CopyOf(bytes, VortexLimits.MaxAlignment) };

    internal readonly ReadOnlySpan<byte> Span => _owner is { } owner ? owner.Buffer.Span : default;

    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
    }
}
