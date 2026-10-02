using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>The repository's <c>tests/Vorticity.Zstd.Tests/testdata/</c>, found by walking up from this source file.</summary>
internal static class TestData
{
    private static readonly Lazy<string> Root = new(() => Find());

    public static string Directory => Root.Value;

    public static string PathOf(string relative) => Path.Combine(Directory, relative);

    public static byte[] Read(string relative) => File.ReadAllBytes(PathOf(relative));

    private static string Find([CallerFilePath] string here = "")
    {
        string? directory = Path.GetDirectoryName(here);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "testdata");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("testdata/ not found above " + here);
    }
}
