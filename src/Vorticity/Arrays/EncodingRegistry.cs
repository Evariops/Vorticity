// Phase 1 contract §2.3: ids are classified at OPEN time and failure happens at USE time. This
// file is the classifier. It is a hand-written UTF-8 matcher - switch on length, then on one
// discriminating byte, then SequenceEqual - and never a Dictionary<string, ...>: a dictionary keyed
// by string would force an allocation and a hash of file-controlled bytes on every lookup.
//
// An id we do not implement resolves to Unknown, and THAT IS NOT AN ERROR here. The three places
// that turn Unknown into a VortexUnsupportedException are named in contract §2.3:
// ArrayDecoderTable.Get, LayoutReaderTable.Get and ExtensionDTypeRegistry.RequireSupported.
//
// Membership was cross-checked against spec/editions/core*.toml (the union of every frozen
// edition) and against corpus/manifest.json's coverage.covered / coverage.unclaimed_observed.
using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays;

/// <summary>
/// Every array encoding Phase 1 can decode, plus <see cref="Unknown"/>. The values are
/// <em>ours</em>: they are not the file's <c>u16</c> spec index and must never be persisted,
/// compared against a wire value, or assumed stable across files.
/// </summary>
public enum ArrayEncodingId : ushort
{
    /// <summary>Not an encoding this build decodes. Legal at parse time; fatal only at use.</summary>
    Unknown = 0,

    /// <summary><c>vortex.null</c>.</summary>
    Null,

    /// <summary><c>vortex.bool</c>.</summary>
    Bool,

    /// <summary><c>vortex.primitive</c>.</summary>
    Primitive,

    /// <summary><c>vortex.decimal</c>.</summary>
    Decimal,

    /// <summary><c>vortex.varbin</c>.</summary>
    VarBin,

    /// <summary><c>vortex.varbinview</c>.</summary>
    VarBinView,

    /// <summary><c>vortex.struct</c>.</summary>
    Struct,

    /// <summary><c>vortex.list</c> (the array; the layout of the same name is out of scope).</summary>
    List,

    /// <summary><c>vortex.listview</c>.</summary>
    ListView,

    /// <summary><c>vortex.fixed_size_list</c>.</summary>
    FixedSizeList,

    /// <summary><c>vortex.ext</c>.</summary>
    Extension,

    /// <summary><c>vortex.chunked</c> (the array).</summary>
    Chunked,

    /// <summary><c>vortex.constant</c>.</summary>
    Constant,

    /// <summary><c>vortex.masked</c>.</summary>
    Masked,

    /// <summary><c>fastlanes.for</c>.</summary>
    FastLanesFor,

    /// <summary><c>fastlanes.bitpacked</c>.</summary>
    FastLanesBitPacked,

    /// <summary><c>fastlanes.delta</c>.</summary>
    FastLanesDelta,

    /// <summary><c>fastlanes.rle</c>.</summary>
    FastLanesRle,

    /// <summary><c>vortex.zigzag</c>.</summary>
    ZigZag,

    /// <summary><c>vortex.runend</c>.</summary>
    RunEnd,

    /// <summary><c>vortex.dict</c> (the array).</summary>
    Dict,

    /// <summary><c>vortex.sparse</c>.</summary>
    Sparse,

    /// <summary><c>vortex.sequence</c>.</summary>
    Sequence,

    /// <summary><c>vortex.bytebool</c>.</summary>
    ByteBool,

    // ---------------------------------------------------------------- Phase 2 (docs/90-registry.md)

    /// <summary><c>vortex.decimal_byte_parts</c>.</summary>
    DecimalByteParts,

    /// <summary><c>vortex.datetimeparts</c>.</summary>
    DateTimeParts,

    /// <summary><c>vortex.zstd</c>.</summary>
    Zstd,

    /// <summary><c>vortex.alp</c>.</summary>
    Alp,

    /// <summary><c>vortex.alprd</c>.</summary>
    AlpRd,

    /// <summary><c>vortex.fsst</c>.</summary>
    Fsst,

    /// <summary><c>vortex.onpair</c>.</summary>
    OnPair,
}

/// <summary>
/// Every layout encoding Phase 1 can read, plus <see cref="Unknown"/>. Ours, not the file's.
/// </summary>
public enum LayoutEncodingId : ushort
{
    /// <summary>Not a layout this build reads. Fatal only when it is on the path to projected data.</summary>
    Unknown = 0,

    /// <summary><c>vortex.flat</c>.</summary>
    Flat,

    /// <summary><c>vortex.chunked</c>.</summary>
    Chunked,

    /// <summary><c>vortex.struct</c>.</summary>
    Struct,

    /// <summary><c>vortex.dict</c>.</summary>
    Dict,

    /// <summary><c>vortex.zoned</c>.</summary>
    Zoned,

    /// <summary><c>vortex.stats</c>, the legacy ancestor of <c>vortex.zoned</c> (contract §2.7).</summary>
    Stats,
}

/// <summary>
/// Resolves wire component ids to the enums above. Allocation-free and side-effect-free.
/// </summary>
public static class EncodingRegistry
{
    /// <summary>The highest defined <see cref="ArrayEncodingId"/>; the decoder table is sized by it.</summary>
    internal const int MaxArrayEncodingId = (int)ArrayEncodingId.OnPair;

    /// <summary>The highest defined <see cref="LayoutEncodingId"/>.</summary>
    internal const int MaxLayoutEncodingId = (int)LayoutEncodingId.Stats;

    /// <summary>
    /// Resolves an array encoding id. An id this build does not decode - a future edition's, or
    /// one Phase 1 deferred - returns <see cref="ArrayEncodingId.Unknown"/>, which is
    /// <em>not</em> an error (contract §2.3).
    /// </summary>
    /// <param name="idUtf8">The id exactly as the footer's <c>array_specs</c> carries it.</param>
    public static ArrayEncodingId ResolveArray(ReadOnlySpan<byte> idUtf8)
    {
        // Length first, then one discriminating byte, then a full compare. Byte 7 is the first
        // character after "vortex." and separates almost every same-length pair on its own.
        switch (idUtf8.Length)
        {
            case 10:
                switch (idUtf8[7])
                {
                    case (byte)'e': return idUtf8.SequenceEqual("vortex.ext"u8) ? ArrayEncodingId.Extension : ArrayEncodingId.Unknown;
                    case (byte)'a': return idUtf8.SequenceEqual("vortex.alp"u8) ? ArrayEncodingId.Alp : ArrayEncodingId.Unknown;
                    default: return ArrayEncodingId.Unknown;
                }

            case 11:
                switch (idUtf8[7])
                {
                    case (byte)'n': return idUtf8.SequenceEqual("vortex.null"u8) ? ArrayEncodingId.Null : ArrayEncodingId.Unknown;
                    case (byte)'b': return idUtf8.SequenceEqual("vortex.bool"u8) ? ArrayEncodingId.Bool : ArrayEncodingId.Unknown;
                    case (byte)'l': return idUtf8.SequenceEqual("vortex.list"u8) ? ArrayEncodingId.List : ArrayEncodingId.Unknown;
                    case (byte)'d': return idUtf8.SequenceEqual("vortex.dict"u8) ? ArrayEncodingId.Dict : ArrayEncodingId.Unknown;
                    case (byte)'z': return idUtf8.SequenceEqual("vortex.zstd"u8) ? ArrayEncodingId.Zstd : ArrayEncodingId.Unknown;
                    case (byte)'f': return idUtf8.SequenceEqual("vortex.fsst"u8) ? ArrayEncodingId.Fsst : ArrayEncodingId.Unknown;
                    default: return ArrayEncodingId.Unknown;
                }

            case 12:
                return idUtf8.SequenceEqual("vortex.alprd"u8)
                    ? ArrayEncodingId.AlpRd
                    : ArrayEncodingId.Unknown;

            case 13:
                switch (idUtf8[7])
                {
                    case (byte)'v': return idUtf8.SequenceEqual("vortex.varbin"u8) ? ArrayEncodingId.VarBin : ArrayEncodingId.Unknown;
                    case (byte)'m': return idUtf8.SequenceEqual("vortex.masked"u8) ? ArrayEncodingId.Masked : ArrayEncodingId.Unknown;
                    case (byte)'z': return idUtf8.SequenceEqual("vortex.zigzag"u8) ? ArrayEncodingId.ZigZag : ArrayEncodingId.Unknown;
                    case (byte)'o': return idUtf8.SequenceEqual("vortex.onpair"u8) ? ArrayEncodingId.OnPair : ArrayEncodingId.Unknown;
                    case (byte)'r': return idUtf8.SequenceEqual("vortex.runend"u8) ? ArrayEncodingId.RunEnd : ArrayEncodingId.Unknown;
                    case (byte)'s':
                        // "vortex.struct" and "vortex.sparse" share byte 7; byte 8 splits them.
                        if (idUtf8.SequenceEqual("vortex.struct"u8))
                        {
                            return ArrayEncodingId.Struct;
                        }

                        return idUtf8.SequenceEqual("vortex.sparse"u8) ? ArrayEncodingId.Sparse : ArrayEncodingId.Unknown;
                    case (byte)'e':
                        // Byte 7 of "fastlanes.for" / "fastlanes.rle" is the 'e' of "fastlanes",
                        // not the '.': they are the only 13-byte ids outside the "vortex." family.
                        if (idUtf8.SequenceEqual("fastlanes.for"u8))
                        {
                            return ArrayEncodingId.FastLanesFor;
                        }

                        return idUtf8.SequenceEqual("fastlanes.rle"u8) ? ArrayEncodingId.FastLanesRle : ArrayEncodingId.Unknown;
                    default: return ArrayEncodingId.Unknown;
                }

            case 14:
                switch (idUtf8[7])
                {
                    case (byte)'d': return idUtf8.SequenceEqual("vortex.decimal"u8) ? ArrayEncodingId.Decimal : ArrayEncodingId.Unknown;
                    case (byte)'c': return idUtf8.SequenceEqual("vortex.chunked"u8) ? ArrayEncodingId.Chunked : ArrayEncodingId.Unknown;
                    default: return ArrayEncodingId.Unknown;
                }

            case 15:
                switch (idUtf8[7])
                {
                    case (byte)'l': return idUtf8.SequenceEqual("vortex.listview"u8) ? ArrayEncodingId.ListView : ArrayEncodingId.Unknown;
                    case (byte)'c': return idUtf8.SequenceEqual("vortex.constant"u8) ? ArrayEncodingId.Constant : ArrayEncodingId.Unknown;
                    case (byte)'s': return idUtf8.SequenceEqual("vortex.sequence"u8) ? ArrayEncodingId.Sequence : ArrayEncodingId.Unknown;
                    case (byte)'b': return idUtf8.SequenceEqual("vortex.bytebool"u8) ? ArrayEncodingId.ByteBool : ArrayEncodingId.Unknown;

                    // Byte 7 of "fastlanes.delta" is the 'e' of "fastlanes", the same trap the
                    // 13-byte case documents: it is not the '.' the "vortex." ids put there.
                    case (byte)'e': return idUtf8.SequenceEqual("fastlanes.delta"u8) ? ArrayEncodingId.FastLanesDelta : ArrayEncodingId.Unknown;
                    default: return ArrayEncodingId.Unknown;
                }

            case 16:
                return idUtf8.SequenceEqual("vortex.primitive"u8) ? ArrayEncodingId.Primitive : ArrayEncodingId.Unknown;

            case 17:
                return idUtf8.SequenceEqual("vortex.varbinview"u8) ? ArrayEncodingId.VarBinView : ArrayEncodingId.Unknown;

            case 19:
                return idUtf8.SequenceEqual("fastlanes.bitpacked"u8)
                    ? ArrayEncodingId.FastLanesBitPacked
                    : ArrayEncodingId.Unknown;

            case 22:
                return idUtf8.SequenceEqual("vortex.fixed_size_list"u8)
                    ? ArrayEncodingId.FixedSizeList
                    : ArrayEncodingId.Unknown;

            case 20:
                return idUtf8.SequenceEqual("vortex.datetimeparts"u8)
                    ? ArrayEncodingId.DateTimeParts
                    : ArrayEncodingId.Unknown;

            case 25:
                return idUtf8.SequenceEqual("vortex.decimal_byte_parts"u8)
                    ? ArrayEncodingId.DecimalByteParts
                    : ArrayEncodingId.Unknown;

            default:
                return ArrayEncodingId.Unknown;
        }
    }

    /// <summary>
    /// Resolves a layout encoding id. Unrecognized returns <see cref="LayoutEncodingId.Unknown"/>,
    /// which is not an error until the layout is on the path to projected data (contract §2.3).
    /// </summary>
    /// <param name="idUtf8">The id exactly as the footer's <c>layout_specs</c> carries it.</param>
    public static LayoutEncodingId ResolveLayout(ReadOnlySpan<byte> idUtf8)
    {
        switch (idUtf8.Length)
        {
            case 11:
                switch (idUtf8[7])
                {
                    case (byte)'f': return idUtf8.SequenceEqual("vortex.flat"u8) ? LayoutEncodingId.Flat : LayoutEncodingId.Unknown;
                    case (byte)'d': return idUtf8.SequenceEqual("vortex.dict"u8) ? LayoutEncodingId.Dict : LayoutEncodingId.Unknown;
                    default: return LayoutEncodingId.Unknown;
                }

            case 12:
                switch (idUtf8[7])
                {
                    case (byte)'z': return idUtf8.SequenceEqual("vortex.zoned"u8) ? LayoutEncodingId.Zoned : LayoutEncodingId.Unknown;
                    case (byte)'s': return idUtf8.SequenceEqual("vortex.stats"u8) ? LayoutEncodingId.Stats : LayoutEncodingId.Unknown;
                    default: return LayoutEncodingId.Unknown;
                }

            case 13:
                return idUtf8.SequenceEqual("vortex.struct"u8) ? LayoutEncodingId.Struct : LayoutEncodingId.Unknown;

            case 14:
                return idUtf8.SequenceEqual("vortex.chunked"u8) ? LayoutEncodingId.Chunked : LayoutEncodingId.Unknown;

            default:
                return LayoutEncodingId.Unknown;
        }
    }

    /// <summary>
    /// A short note for ids we know of but do not decode, used as the <c>detail</c> argument of
    /// <see cref="VortexUnsupportedException"/>. <see langword="null"/> when we have nothing
    /// useful to say - a genuinely unknown id from a future edition.
    /// </summary>
    /// <param name="idUtf8">The id exactly as the file carries it.</param>
    /// <remarks>
    /// The notes contract §2.8 pins verbatim are <c>vortex.patched</c> and the
    /// <c>vortex.list</c> <em>layout</em>; <c>fastlanes.delta</c> was a third until it gained a
    /// decoder. The array <c>vortex.list</c> is implemented, so it never reaches a throw site and
    /// the layout note is unambiguous here.
    /// </remarks>
    public static string? DescribeUnsupported(ReadOnlySpan<byte> idUtf8)
    {
        // Cold path: readability beats a second hand-rolled trie.
        //
        // `fastlanes.delta` used to be described here as "in no core edition; a default writer
        // cannot emit it". Both halves are still true of UPSTREAM, and neither was ever a reason not
        // to read one, so the entry went when the decoder arrived rather than the sentence being
        // reworded. Contract §2.8 pins the remaining two.
        if (idUtf8.SequenceEqual("vortex.patched"u8))
        {
            return "in-memory only upstream; never produced by a conformant writer";
        }

        if (idUtf8.SequenceEqual("vortex.list"u8))
        {
            return "experimental list layout; in no core edition";
        }

        if (idUtf8.SequenceEqual("vortex.zstd_buffers"u8) ||
            idUtf8.SequenceEqual("vortex.pco"u8) ||
            idUtf8.SequenceEqual("vortex.map"u8) ||
            idUtf8.SequenceEqual("vortex.variant"u8) ||
            idUtf8.SequenceEqual("vortex.parquet.variant"u8))
        {
            return "deferred upstream to Vortex 1.1; not implemented";
        }

        return null;
    }

    /// <summary>
    /// <see cref="DescribeUnsupported(ReadOnlySpan{byte})"/> for a caller that only has the id as
    /// a <see cref="string"/>. Cold path only: it transcodes onto the stack.
    /// </summary>
    /// <param name="id">The id text.</param>
    internal static string? DescribeUnsupported(string id)
    {
        if (id is null)
        {
            return null;
        }

        // Every id we have a note for is far below this; a longer one has no note by definition.
        Span<byte> utf8 = stackalloc byte[64];
        if (!System.Text.Encoding.UTF8.TryGetBytes(id, utf8, out int written))
        {
            return null;
        }

        return DescribeUnsupported(utf8[..written]);
    }

    /// <summary>Guards a value cast from a file-supplied index.</summary>
    /// <param name="id">The candidate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsDefined(ArrayEncodingId id) => (uint)id <= MaxArrayEncodingId;

    /// <summary>Guards a value cast from a file-supplied index.</summary>
    /// <param name="id">The candidate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsDefined(LayoutEncodingId id) => (uint)id <= MaxLayoutEncodingId;
}
