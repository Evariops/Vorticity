// What a key may be - docs/13-dataset.md §3, where the two kinds are `data/<uid>.vortex` and
// `commit/<inverted version>.vxc`.
//
// THE RULES ARE THE FILE STORE'S, and every store gets them. A key is a path in an object store
// and a path on a disk, and the second is the narrower of the two: a store that accepted `..` or a
// leading slash would let a dataset write outside its own prefix the moment someone pointed a file
// store at it. Checking in one place means the memory store refuses exactly what the file store
// cannot represent, so a test that passes against one passes against the other -- which is the
// contract suite's whole premise.
using System;

namespace Vorticity.Dataset;

/// <summary>The key rules every <see cref="IObjectStore"/> enforces.</summary>
public static class ObjectKey
{
    /// <summary>The longest key a store accepts, in UTF-16 units.</summary>
    /// <remarks>
    /// S3 allows 1 024 bytes of UTF-8 and a file system a few hundred per component; this is under
    /// both and far above what §3's two shapes need (a key there is about thirty characters).
    /// </remarks>
    public const int MaxLength = 512;

    /// <summary>Throws unless <paramref name="key"/> is a usable key.</summary>
    /// <param name="key">The candidate.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
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

    /// <summary>Whether <paramref name="key"/> is a usable key.</summary>
    /// <param name="key">The candidate.</param>
    /// <returns>Whether <see cref="Check"/> would accept it.</returns>
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
