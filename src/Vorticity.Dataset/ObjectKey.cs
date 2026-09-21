using System;

namespace Vorticity.Dataset;

/// <summary>
/// The key rules every <see cref="IObjectStore"/> enforces, including stores implemented outside
/// this repository. They are the narrower file-system rules, so that a key can never reach outside
/// a dataset's own prefix and every store refuses exactly the same keys.
/// </summary>
internal static class ObjectKey
{
    /// <summary>
    /// The longest key a store accepts, in UTF-16 units: under what object stores and file systems
    /// allow, and far above what the dataset's own key shapes need.
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>Throws unless <paramref name="key"/> is a usable key.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">It breaks one of the rules.</exception>
    public static void Check(string key, string parameterName = "key")
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.Length == 0)
        {
            throw new ArgumentException("A key is not empty.", parameterName);
        }

        if (key.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A key is at most {MaxLength} characters; this one is {key.Length}.", parameterName);
        }

        if (key[0] == '/' || key[^1] == '/')
        {
            throw new ArgumentException($"A key neither starts nor ends with '/': '{key}'.", parameterName);
        }

        foreach (char c in key)
        {
            // Printable ASCII only. A store's keys end up in a URL, in a path and in a listing's
            // ordinal order; anything outside this range makes at least one of the three surprising.
            if (c is < ' ' or > '~' || c == '\\')
            {
                throw new ArgumentException(
                    $"A key holds printable ASCII other than '\\': '{key}'.", parameterName);
            }
        }

        if (key.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"A key holds no empty segment: '{key}'.", parameterName);
        }

        foreach (Range segment in key.AsSpan().Split('/'))
        {
            ReadOnlySpan<char> part = key.AsSpan()[segment];
            if (part is "." or "..")
            {
                throw new ArgumentException($"A key holds no '.' or '..' segment: '{key}'.", parameterName);
            }
        }
    }

    /// <summary>Whether <see cref="Check"/> would accept <paramref name="key"/>.</summary>
    public static bool IsValid(string? key)
    {
        if (key is null)
        {
            return false;
        }

        try
        {
            Check(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
