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

    /// <summary>
    /// On the Linux this runs on, <c>/tmp</c> is judged by its own mount, the last one <c>/proc/mounts</c> lists for it, and
    /// the scratch of a session that names none leaves it for the user's cache when it lies in memory, as it does on the
    /// CI's runners: the choice the test above holds to a map of mounts, on the machine's.
    /// </summary>
    [Fact]
    public void OnLinuxTheTemporaryDirectoryIsJudgedByItsOwnMount()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the mounts are Linux's");
        string? type = null;
        foreach (string line in System.IO.File.ReadLines("/proc/mounts"))
        {
            string[] fields = line.Split(' ');
            if (fields.Length >= 3 && fields[1] == "/tmp")
            {
                type = fields[2];
            }
        }

        bool tmpfs = type is "tmpfs" or "ramfs";
        Assert.Equal(tmpfs, Vorticity.Aggregating.GroupCore.InMemory("/tmp/"));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string chosen = ScratchDirectories.Choose("/tmp/", cache, home, Vorticity.Aggregating.GroupCore.InMemory);
        string expected = Path.Combine(string.IsNullOrEmpty(cache) ? Path.Combine(home, ".cache") : cache, "vorticity");
        Assert.Equal(tmpfs && !Vorticity.Aggregating.GroupCore.InMemory(expected) ? expected : "/tmp/", chosen);
        Console.Out.Write($"SCRATCH /tmp is {type ?? "not a mount of its own"}: a session's scratch goes to {chosen}.\n");
    }
}
