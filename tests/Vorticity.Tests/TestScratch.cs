// The directories the tests that spill name as their scratch. A directory a session names is refused where its scratch is
// written when it lies on a file system in memory, as /tmp does on recent Linux; one made under the temporary directory
// would hold there only while TMPDIR points at a disk. These lie under the library's own default instead, which takes the
// user's cache on a disk in that case.
using System;
using System.IO;

namespace Vorticity.Tests;

/// <summary>Scratch directories for the tests that spill, under the library's default scratch directory.</summary>
internal static class TestScratch
{
    /// <summary>A new, empty directory whose name starts with <paramref name="prefix"/>.</summary>
    internal static string Create(string prefix)
    {
        string path = Path.Combine(ScratchDirectories.Of(null), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
