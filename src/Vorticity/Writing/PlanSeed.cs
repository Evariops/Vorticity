// The plan memory an appended file starts from (docs/11-write-strategy.md §3.8).
//
// "PLAN MEMORY IS SEEDED FROM THE LAST CHUNK'S ENCODING TREE, so that the appended data keeps the
// file's encodings unless its statistics say otherwise." The tree is read once, from the last
// chunk's segment of each column, and turned into what `ColumnWriter.Remember` would have left had
// the same writer written that chunk a moment ago: a scheme per column, and per struct field below
// it, since those are the nodes that keep a memory.
//
// A MEMORY THAT HELD, BY CONSTRUCTION. The old chunk's bytes are the bytes its plan produced, so the
// prediction is taken as met. The chooser still re-prices the scheme on the new chunk's statistics
// and keeps it only if it wins on its own terms (§3.4.3); what the seed skips is the full pricing of
// the first chunk, and what it turns on is the ingest state that scheme reads -- the distinct table
// under a dictionary, the width histograms under a bit-packing whose widths are the raw or the
// zigzag ones.
//
// NOTHING IS SEEDED FROM A CANONICAL CHUNK. A plain array says no scheme won, which full pricing
// finds again for the price of one chunk; seeding it would hold a large append to the verdict of a
// short last chunk, which the chooser leaves canonical without pricing anything (`MinimumRows`). An
// encoding this writer does not choose -- a reference file's `vortex.pco`, a masked wrapper -- seeds
// nothing either.
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>What one node of an old chunk was written as.</summary>
internal sealed class PlanSeed
{
    private PlanSeed(ColumnScheme? scheme, bool widthsServe, PlanSeed?[] fields)
    {
        Scheme = scheme;
        WidthsServe = widthsServe;
        Fields = fields;
    }

    /// <summary>The node's scheme, or <see langword="null"/> when it seeds no memory of its own.</summary>
    internal ColumnScheme? Scheme { get; }

    /// <summary>
    /// Whether a bit-packing packed the raw or the zigzag widths -- no frame of reference -- which is
    /// when the ingested width histograms price it (<c>ColumnWriter.Remember</c>).
    /// </summary>
    internal bool WidthsServe { get; }

    /// <summary>Per child column (a struct's fields, an extension's storage), its seed or none.</summary>
    internal PlanSeed?[] Fields { get; }

    /// <summary>The seed of a column chunk's root, or <see langword="null"/> when it says nothing.</summary>
    /// <param name="node">The chunk's root array node.</param>
    /// <param name="dtype">The column's dtype.</param>
    internal static PlanSeed? Of(ArrayNode node, DType dtype)
    {
        if (dtype.Kind == DTypeKind.Extension)
        {
            if (node.Encoding != ArrayEncodingId.Extension || node.ChildCount != 1)
            {
                return null;
            }

            PlanSeed? storage = Of(node.GetChild(0), dtype.StorageType);
            return storage is null ? null : new PlanSeed(null, false, [storage]);
        }

        if (dtype.Kind == DTypeKind.Struct)
        {
            // A struct puts its validity first, when it has one.
            int fields = dtype.FieldCount;
            int offset = node.ChildCount - fields;
            if (node.Encoding != ArrayEncodingId.Struct || offset is not (0 or 1) || fields == 0)
            {
                return null;
            }

            PlanSeed?[] seeds = new PlanSeed?[fields];
            bool any = false;
            for (int i = 0; i < fields; i++)
            {
                seeds[i] = Of(node.GetChild(offset + i), dtype.GetField(i));
                any |= seeds[i] is not null;
            }

            return any ? new PlanSeed(null, false, seeds) : null;
        }

        ColumnScheme? scheme = SchemeOf(node.Encoding);
        return scheme is null
            ? null
            : new PlanSeed(
                scheme,
                node.Encoding is ArrayEncodingId.FastLanesBitPacked or ArrayEncodingId.ZigZag,
                []);
    }

    /// <summary>
    /// The scheme that writes <paramref name="encoding"/> at the top of a column: the names
    /// <c>ArrayBlobWriter</c> emits for each, and no other (the ALP scheme never writes ALP-RD).
    /// </summary>
    /// <param name="encoding">A chunk's root encoding.</param>
    internal static ColumnScheme? SchemeOf(ArrayEncodingId encoding) => encoding switch
    {
        ArrayEncodingId.Dict => ColumnScheme.Dict,
        ArrayEncodingId.RunEnd => ColumnScheme.RunEnd,
        ArrayEncodingId.FastLanesBitPacked or ArrayEncodingId.FastLanesFor or ArrayEncodingId.ZigZag
            => ColumnScheme.BitPacked,
        ArrayEncodingId.Sequence => ColumnScheme.Sequence,
        ArrayEncodingId.Alp => ColumnScheme.Alp,
        ArrayEncodingId.Fsst => ColumnScheme.Fsst,
        ArrayEncodingId.Zstd => ColumnScheme.Zstd,
        _ => null,
    };
}
