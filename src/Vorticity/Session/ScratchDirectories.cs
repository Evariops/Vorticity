using System;
using System.IO;
using Vorticity.Aggregating;
using Vorticity.Diagnostics;

namespace Vorticity;

/// <summary>
/// Where the scratch of a session goes when its options name no directory: the system's temporary directory, unless it
/// lies on a file system in memory, as <c>/tmp</c> does on recent Linux, where spilling would take the memory it gives
/// back. A directory of the user's cache on a disk then takes its place, <c>$XDG_CACHE_HOME/vorticity</c> or
/// <c>~/.cache/vorticity</c>, which an event says once (<see cref="VortexEventSource.ScratchMoved"/>).
/// </summary>
/// <remarks>
/// A directory a session names is its own: one on a file system in memory is refused where the scratch is written, with a
/// message that says so, as is the default when no directory of the cache can take its place.
/// </remarks>
internal static class ScratchDirectories
{
    // The default, worked out once for the process: the temporary directory and the mounts do not move under it.
    private static string? s_default;

    /// <summary>The directory a session's scratch goes to: <paramref name="named"/>, or the default when it names none.</summary>
    internal static string Of(string? named) => named ?? (s_default ??= Default());

    /// <summary>
    /// The default among the temporary directory <paramref name="temporary"/> and the cache's: the cache's when the
    /// temporary one lies in memory, <paramref name="inMemory"/> says, and the cache's does not.
    /// </summary>
    /// <param name="temporary">The system's temporary directory.</param>
    /// <param name="cache">The user's cache, <c>$XDG_CACHE_HOME</c>; null or empty for none.</param>
    /// <param name="home">The user's home, whose <c>.cache</c> stands in for an unset cache.</param>
    /// <param name="inMemory">Whether a directory lies on a file system in memory.</param>
    internal static string Choose(string temporary, string? cache, string home, Func<string, bool> inMemory)
    {
        if (!inMemory(temporary))
        {
            return temporary;
        }

        string directory = Path.Combine(string.IsNullOrEmpty(cache) ? Path.Combine(home, ".cache") : cache, "vorticity");
        return inMemory(directory) ? temporary : directory;
    }

    private static string Default()
    {
        string temporary = Path.GetTempPath();
        string chosen = Choose(
            temporary,
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            GroupCore.InMemory);
        if (string.Equals(chosen, temporary, StringComparison.Ordinal))
        {
            return temporary;
        }

        try
        {
            Directory.CreateDirectory(chosen);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return temporary;
        }

        VortexEventSource.Log.ScratchMoved(temporary, chosen);
        return chosen;
    }
}
