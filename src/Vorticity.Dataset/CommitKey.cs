// The key of a commit object - docs/13-dataset.md §3: "`<inverted version>` is 10²⁰ − 1 − version,
// twenty digits: the newest commit sorts first, so one `List(prefix: "commit/", max: 1)` returns it
// with no hint and no probe (§8.3). There is no mutable object anywhere in the layout."
//
// WHY INVERSION RATHER THAN A POINTER. Every other design needs a mutable object -- a `HEAD` file,
// a table row, a rename -- and an object store has none: `PutIfAbsent` creates, nothing updates. So
// the newest version is found by ORDER instead, and the order is the store's own: ordinal over
// keys, which every store gives and §11 requires to be strongly consistent. Inverting the version
// turns "the largest version" into "the first key", which one listing of one key answers.
//
// TWENTY DIGITS, ZERO-PADDED, because ordinal order over strings is digit order only when the
// strings are the same length: "9" sorts after "10" and 09 does not. 10²⁰ − 1 is the largest value
// twenty digits hold, and it is over `ulong.MaxValue`, so every version this library can count to
// has an inverse that fits.
using System;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>The keys of a dataset's two kinds of object (§3).</summary>
public static class CommitKey
{
    /// <summary>The prefix every commit object's key starts with.</summary>
    public const string Prefix = "commit/";

    /// <summary>The suffix every commit object's key ends with.</summary>
    public const string Suffix = ".vxc";

    /// <summary>The prefix every data object's key starts with.</summary>
    public const string DataPrefix = "data/";

    /// <summary>The suffix every data object's key ends with.</summary>
    public const string DataSuffix = ".vortex";

    /// <summary>Digits of the inverted version: 10²⁰ − 1 is the largest of them.</summary>
    public const int Digits = 20;

    /// <summary>10²⁰ − 1, which no <see cref="ulong"/> version reaches.</summary>
    private static readonly UInt128 Ceiling = UInt128.Parse("99999999999999999999", CultureInfo.InvariantCulture);

    /// <summary>The key of version <paramref name="version"/>.</summary>
    /// <param name="version">The version; never 0, which is "no parent".</param>
    /// <returns>The key, of the form <c>commit/&lt;inverted version&gt;.vxc</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is 0.</exception>
    public static string For(ulong version)
    {
        ArgumentOutOfRangeException.ThrowIfZero(version);
        UInt128 inverted = Ceiling - version;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{inverted.ToString("D20", CultureInfo.InvariantCulture)}{Suffix}");
    }

    /// <summary>The version a commit object's key names.</summary>
    /// <param name="key">The key.</param>
    /// <param name="version">Receives the version.</param>
    /// <returns>Whether the key is a commit object's.</returns>
    public static bool TryParse(string? key, out ulong version)
    {
        version = 0;
        if (key is null
            || key.Length != Prefix.Length + Digits + Suffix.Length
            || !key.StartsWith(Prefix, StringComparison.Ordinal)
            || !key.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> digits = key.AsSpan(Prefix.Length, Digits);
        if (!UInt128.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out UInt128 inverted)
            || inverted > Ceiling)
        {
            return false;
        }

        UInt128 value = Ceiling - inverted;
        if (value == 0 || value > ulong.MaxValue)
        {
            return false;
        }

        version = (ulong)value;
        return true;
    }

    /// <summary>The key of a data object.</summary>
    /// <param name="uid">Its unique name, usually a <see cref="Guid"/>'s 32 hex digits.</param>
    /// <returns>The key, of the form <c>data/&lt;uid&gt;.vortex</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="uid"/> is empty or holds a slash.</exception>
    public static string ForData(string uid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        if (uid.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A data object's name holds no slash.", nameof(uid));
        }

        return DataPrefix + uid + DataSuffix;
    }
}
