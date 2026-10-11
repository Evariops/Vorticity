namespace Vorticity;

/// <summary>What the writer optimises for when it prices a column's encodings.</summary>
public enum CompressionProfile : byte
{
    /// <summary>Size and decode speed together, per column chunk.</summary>
    Auto,

    /// <summary>The cheapest encodings to write, and no index.</summary>
    Fastest,

    /// <summary>
    /// Size over decode speed: every column chunk is priced by its bytes alone, run-end among the
    /// other candidates, and zstd, pco, FSST, ALP and ALP-RD are all tried under the bytes of the
    /// best exact plan, with no margin kept for a faster decode, the smallest taken -- on the
    /// column and on every array an encoding makes of it, an ALP's integers, a dictionary's codes
    /// or a run's values, as the reference's compact compressor does. A trial that beats an exact
    /// plan is held to what that plan writes, children included, not to its price. The write costs
    /// more, and a read may decode slower.
    /// </summary>
    Smallest,

    /// <summary>Every column canonical: no encoding at all.</summary>
    None,
}
