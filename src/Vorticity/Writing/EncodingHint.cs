using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// The scheme a column is written with, when the caller pins one. These name schemes, not wire
/// encodings: one scheme may write more than one array id.
/// </summary>
public enum EncodingHint : byte
{
    /// <summary>No hint: the chooser prices the candidates, which is the default.</summary>
    Auto = 0,

    /// <summary>The canonical array, uncompressed.</summary>
    Canonical = 1,

    /// <summary>Run-end: one entry per run.</summary>
    RunEnd = 2,

    /// <summary>A dictionary: one code per row, one entry per distinct value.</summary>
    Dictionary = 3,

    /// <summary>A frame of reference, then bit-packing.</summary>
    BitPacked = 4,

    /// <summary>FSST, for text a symbol table captures.</summary>
    Fsst = 5,

    /// <summary>ALP, for decimal-shaped floats.</summary>
    Alp = 6,

    /// <summary>An arithmetic progression, in the metadata.</summary>
    Sequence = 7,

    /// <summary>Zstd over the canonical buffers.</summary>
    Zstd = 8,

    /// <summary>
    /// ALP-RD, for floats with no short decimal form: their high bits in a dictionary of eight and
    /// the rest bit-packed, which decodes at bit-packing's speed and a row at a time on a take.
    /// </summary>
    AlpRd = 9,

    /// <summary>
    /// A decimal as its unscaled integers, at the narrowest signed width that holds them, which then
    /// take the integer schemes: what the writer does with every decimal whose values fit 64 bits.
    /// </summary>
    DecimalByteParts = 10,

    /// <summary>
    /// One value, or nulls only, on every row of a chunk: the scalar and the count. What the writer
    /// does with every such chunk whatever the hint, as it writes a progression, so pinning it
    /// changes nothing; it names the chunks a report says were written so.
    /// </summary>
    Constant = 11,

    /// <summary>
    /// A timestamp as its days since the epoch, its seconds within the day and its units within
    /// the second, each taking the integer schemes: what the writer does with instants coarser than
    /// their unit, instants to the second in microseconds or dates at midnight among them.
    /// </summary>
    DateTimeParts = 12,

    /// <summary>
    /// The valid rows alone, at their positions, and a null everywhere else: what the writer does
    /// with a chunk nulls dominate, nine rows in ten or more, when their runs would cost more.
    /// </summary>
    Sparse = 13,

    /// <summary>
    /// OnPair, for text a dictionary of repeated substrings captures -- URLs, paths, log lines --
    /// each row the longest tokens that spell it, a twelve-bit code each, decoded as fast as FSST.
    /// </summary>
    OnPair = 14,

    /// <summary>
    /// pco, for numbers of sixteen bits and more: entropy-coded bins of their values after a
    /// consecutive delta or a common divisor where either pays -- timestamps, counters, skewed
    /// amounts. The size-first profile tries it on every such column; the smallest files, the
    /// slowest decode.
    /// </summary>
    Pco = 15,
}
