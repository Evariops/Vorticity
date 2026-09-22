using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// What one node of an old chunk was written as, so an append starts from the file's encodings. A
/// canonical array, or an encoding this writer never chooses, seeds nothing.
/// </summary>
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

    /// <summary>Whether the packing used raw or zigzag widths, with no frame of reference.</summary>
    internal bool WidthsServe { get; }

    /// <summary>Per child column (struct field, extension storage, list elements), its seed or none.</summary>
    internal PlanSeed?[] Fields { get; }

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

        if (dtype.Kind is DTypeKind.List or DTypeKind.FixedSizeList or DTypeKind.Map)
        {
            // The elements are the list's only child, whichever of the three list arrays holds them.
            ArrayNode list = node;
            if (dtype.Kind == DTypeKind.Map)
            {
                if (node.Encoding != ArrayEncodingId.Map || node.ChildCount != 1)
                {
                    return null;
                }

                list = node.GetChild(0);
            }

            bool shaped = dtype.Kind == DTypeKind.FixedSizeList
                ? list.Encoding == ArrayEncodingId.FixedSizeList
                : list.Encoding is ArrayEncodingId.ListView or ArrayEncodingId.List;
            if (!shaped || list.ChildCount == 0)
            {
                return null;
            }

            PlanSeed? elements = dtype.Kind == DTypeKind.Map
                ? Entries(list.GetChild(0), dtype)
                : Of(list.GetChild(0), dtype.ElementType);
            return elements is null ? null : new PlanSeed(null, false, [elements]);
        }

        ColumnScheme? scheme = SchemeOf(node.Encoding);
        return scheme is null
            ? null
            : new PlanSeed(
                scheme,
                node.Encoding is ArrayEncodingId.FastLanesBitPacked or ArrayEncodingId.ZigZag,
                []);
    }

    /// <summary>A map's entries: a struct of its key and its value, in that order.</summary>
    private static PlanSeed? Entries(ArrayNode node, DType map)
    {
        int offset = node.ChildCount - 2;
        if (node.Encoding != ArrayEncodingId.Struct || offset is not (0 or 1))
        {
            return null;
        }

        PlanSeed? key = Of(node.GetChild(offset), map.KeyType);
        PlanSeed? value = Of(node.GetChild(offset + 1), map.ValueType);
        return key is null && value is null ? null : new PlanSeed(null, false, [key, value]);
    }

    /// <summary>The scheme that writes this encoding at the top of a column, if any.</summary>
    internal static ColumnScheme? SchemeOf(ArrayEncodingId encoding) => encoding switch
    {
        ArrayEncodingId.Dict => ColumnScheme.Dict,
        ArrayEncodingId.RunEnd => ColumnScheme.RunEnd,
        ArrayEncodingId.FastLanesBitPacked or ArrayEncodingId.FastLanesFor or ArrayEncodingId.ZigZag
            => ColumnScheme.BitPacked,
        ArrayEncodingId.Sequence => ColumnScheme.Sequence,
        ArrayEncodingId.Alp => ColumnScheme.Alp,
        ArrayEncodingId.AlpRd => ColumnScheme.AlpRd,
        ArrayEncodingId.Fsst => ColumnScheme.Fsst,
        ArrayEncodingId.Zstd => ColumnScheme.Zstd,
        _ => null,
    };
}
