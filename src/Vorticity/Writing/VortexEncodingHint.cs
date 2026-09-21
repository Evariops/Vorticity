using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// The scheme a column is written with, when the caller pins one. These name schemes, not wire
/// encodings: one scheme may write more than one array id.
/// </summary>
public enum VortexEncodingHint : byte
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
}
