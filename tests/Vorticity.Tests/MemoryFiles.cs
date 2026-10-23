// Files the tests write to bytes and open as a store would serve them.
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.IO;

namespace Vorticity.Tests;

/// <summary>Files held as bytes.</summary>
internal static class MemoryFiles
{
    /// <summary>The file <paramref name="bytes"/> hold, opened with the default options.</summary>
    internal static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);
}
