namespace Vorticity;

/// <summary>How <see cref="KeyCursor{TKey}.SeekAsync"/> positions itself relative to a key.</summary>
/// <remarks>
/// The five operators are defined on the source's total order over <c>(key, row)</c>, so
/// <see cref="Exact"/> lands on a duplicated key's lowest row and <see cref="AtOrBefore"/> on its
/// highest. A range <c>[a, b)</c> is <see cref="AtOrAfter"/> on <c>a</c> and then <c>NextAsync</c>
/// while the key stays below <c>b</c>.
/// </remarks>
public enum SeekOp : byte
{
    /// <summary>The first entry whose key equals the sought one; invalid when the key is absent.</summary>
    Exact,

    /// <summary>The first entry whose key is at or after the sought one: <c>lower_bound</c>.</summary>
    AtOrAfter,

    /// <summary>The first entry whose key is strictly after: <c>upper_bound</c>, the successor.</summary>
    After,

    /// <summary>The last entry whose key is at or before the sought one.</summary>
    AtOrBefore,

    /// <summary>The last entry whose key is strictly before: the predecessor.</summary>
    Before,
}
