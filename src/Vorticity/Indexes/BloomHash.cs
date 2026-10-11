namespace Vorticity.Indexes;

/// <summary>Which hash a Bloom filter uses.</summary>
internal enum BloomHash
{
    /// <summary>
    /// XxHash3-64, the default and the hash the reference split-block filter uses, so a filter
    /// built here is bit-identical to one built there.
    /// </summary>
    XxHash3 = 0,

    /// <summary>
    /// xxHash64, which makes the filter bit-identical to a Parquet SBBF. A variant for the rare
    /// caller that exchanges filters with Parquet tooling, never the default.
    /// </summary>
    XxHash64 = 1,
}
