using System;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>
/// The keys of a dataset's two kinds of object. A commit's version is stored inverted and
/// zero-padded to a fixed width, so that ordinal order over keys puts the newest commit first and
/// one listing of one key finds it without a mutable pointer anywhere in the layout.
/// </summary>
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

    /// <summary>Digits of the inverted version, enough for every <see cref="ulong"/> version.</summary>
    public const int Digits = 20;

    private static readonly UInt128 Ceiling = UInt128.Parse("99999999999999999999", CultureInfo.InvariantCulture);

    /// <summary>
    /// The key of a version, of the form <c>commit/&lt;inverted version&gt;.vxc</c>. Version 0 is
    /// reserved for "no parent" and has no key.
    /// </summary>
    public static string For(ulong version)
    {
        ArgumentOutOfRangeException.ThrowIfZero(version);
        UInt128 inverted = Ceiling - version;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{inverted.ToString("D20", CultureInfo.InvariantCulture)}{Suffix}");
    }

    /// <summary>The version a commit object's key names, and whether it is one at all.</summary>
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

    /// <summary>
    /// The key of a data object, of the form <c>data/&lt;uid&gt;.vortex</c>, where the uid is
    /// usually a <see cref="Guid"/>'s 32 hex digits.
    /// </summary>
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
