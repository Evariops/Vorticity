using System;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

/// <summary>
/// What the zero-copy design assumes of the host, checked where bytes enter or leave the library:
/// a little-endian host, since file buffers are cast in place, and the sizes of the two inline
/// structs the readers reinterpret, which are a wire contract. Each condition is a constant to the
/// JIT, so a host that meets them pays nothing, and one that does not is refused before it can
/// read byte-swapped or misaligned data.
/// </summary>
internal static class VortexRuntimeChecks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Require()
    {
        if (!BitConverter.IsLittleEndian || Unsafe.SizeOf<SegmentSpec>() != 16 || Unsafe.SizeOf<BufferSpec>() != 8)
        {
            Refuse();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Refuse()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Vorticity requires a little-endian host: the Vortex format is little-endian and " +
                "the reader casts memory-mapped file buffers directly.");
        }

        throw new PlatformNotSupportedException(
            $"SegmentSpec must occupy exactly 16 bytes and BufferSpec 8, as the footer and array " +
            $"flatbuffers lay them out, but this runtime lays them out in {Unsafe.SizeOf<SegmentSpec>()} " +
            $"and {Unsafe.SizeOf<BufferSpec>()}.");
    }
}
