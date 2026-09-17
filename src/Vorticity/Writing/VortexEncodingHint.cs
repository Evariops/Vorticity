// What a caller may pin a column to - docs/11-write-strategy.md §7.1, "EncodingHint per column,
// for callers who know", and §3.4.3, which says what it IS: plan memory with the tolerance set to
// infinity.
//
// THE NAMES ARE THE SCHEMES THE CHOOSER PICKS FROM, not the encodings on the wire: one scheme can
// write more than one array id -- a bit-packing writes `fastlanes.bitpacked` under `fastlanes.for`
// or `vortex.zigzag` as the values ask -- and the caller who knows their data knows the scheme,
// not the framing.
namespace Vorticity.Writing;

/// <summary>The scheme a column is written with, when the caller pins one.</summary>
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
