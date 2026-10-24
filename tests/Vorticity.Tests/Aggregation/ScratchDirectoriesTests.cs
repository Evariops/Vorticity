using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>Where the scratch of a session that names no directory goes.</summary>
public sealed class ScratchDirectoriesTests
{
    private const string Temporary = "/tmp/";

    private const string Home = "/home/ada";

    /// <summary>
    /// The temporary directory while it lies on a disk; past it, on a file system in memory, the user's cache's, its
    /// variable's or the home's, unless that lies in memory too, when the temporary one stays and is refused where the
    /// scratch is written.
    /// </summary>
    [Fact]
    public void TheScratchLeavesATemporaryDirectoryInMemoryForTheCache()
    {
        HashSet<string> memory = new HashSet<string>(StringComparer.Ordinal) { Temporary };
        bool InMemory(string directory) => memory.Contains(directory);

        Assert.Equal(Temporary, ScratchDirectories.Choose(Temporary, null, Home, _ => false));
        Assert.Equal(Path.Combine(Home, ".cache", "vorticity"), ScratchDirectories.Choose(Temporary, null, Home, InMemory));
        Assert.Equal(Path.Combine(Home, ".cache", "vorticity"), ScratchDirectories.Choose(Temporary, string.Empty, Home, InMemory));
        Assert.Equal(Path.Combine("/var/cache/ada", "vorticity"), ScratchDirectories.Choose(Temporary, "/var/cache/ada", Home, InMemory));

        memory.Add(Path.Combine("/var/cache/ada", "vorticity"));
        Assert.Equal(Temporary, ScratchDirectories.Choose(Temporary, "/var/cache/ada", Home, InMemory));
    }
}
