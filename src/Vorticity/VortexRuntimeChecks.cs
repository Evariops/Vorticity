// docs/09-contracts.md §7: the zero-copy design casts file buffers directly with MemoryMarshal.
// That assumes a little-endian host. Fail loudly at load rather than return byte-swapped data.
using System.Runtime.CompilerServices;

namespace Vorticity;

internal static class VortexRuntimeChecks
{
    // CA2255 warns that ModuleInitializer is meant for applications. Here it is deliberate and
    // documented (docs/09-contracts.md §7): the check must run before any caller can hand us a
    // buffer, and it is a single branch executed once.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Deliberate endianness guard; see docs/09-contracts.md §7.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!System.BitConverter.IsLittleEndian)
        {
            throw new System.PlatformNotSupportedException(
                "Vorticity requires a little-endian host: the Vortex format is little-endian and " +
                "the reader casts memory-mapped file buffers directly.");
        }
    }
}
