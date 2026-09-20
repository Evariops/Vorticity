using System.Runtime.CompilerServices;

namespace Vorticity;

/// <summary>
/// Refuses to load on a big-endian host. The zero-copy design casts file buffers directly, which
/// assumes a little-endian host, so failing at load is better than returning byte-swapped data.
/// </summary>
internal static class VortexRuntimeChecks
{
    // CA2255 warns that ModuleInitializer is meant for applications; here it is deliberate. The
    // check must run before any caller can hand the library a buffer, and it is a single branch
    // executed once.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "The endianness guard must run before a caller can hand the library a buffer.")]
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
