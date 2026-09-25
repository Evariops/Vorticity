using System;

namespace Vorticity.Dataset;

/// <summary>
/// A commit object is not whole: its writer stopped before its last byte, which a store that claims a
/// key before it writes the object allows, or its bytes changed since.
/// </summary>
/// <remarks>
/// The newest commit left short by a writer that died keeps every reader and writer of the dataset
/// out until <see cref="VortexDataset.RemoveTornCommitAsync"/> removes it; the dataset then reads as
/// the version before it, and the next commit takes the number again.
/// </remarks>
public sealed class TornCommitException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public TornCommitException()
        : base("A commit object of the dataset is not whole.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which commit, and what to do.</param>
    public TornCommitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">Which commit, and what to do.</param>
    /// <param name="innerException">What the commit's bytes failed.</param>
    public TornCommitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The key of the commit object.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The version the commit object holds, or was to hold.</summary>
    public ulong Version { get; init; }

    /// <summary>The exception for the commit object of <paramref name="version"/>, which <paramref name="cause"/> found not whole.</summary>
    internal static TornCommitException Of(ulong version, Exception cause)
    {
        string key = CommitKey.For(version);
        return new TornCommitException(
            $"Version {version} of the dataset, '{key}', is not a whole commit object: its writer stopped before its " +
            $"last byte, or its bytes changed since ({cause.Message}). When it is the newest commit, " +
            $"{nameof(VortexDataset)}.{nameof(VortexDataset.RemoveTornCommitAsync)} removes it, and the dataset reads " +
            $"as version {version - 1} again.",
            cause)
        {
            Key = key,
            Version = version,
        };
    }
}
