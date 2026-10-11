using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a <c>vortex.list</c> layout: a list column shredded into an elements child, an offsets
/// child and, when the dtype is nullable, a validity child. The offsets child carries
/// <c>rows + 1</c> entries, so element <c>i</c> of row <c>r</c> lives at
/// <c>elements[offsets[r] + i]</c>.
/// </summary>
/// <remarks>
/// The elements child is registered and read whole, and the window is taken afterwards. Reading it
/// selectively would mean fetching the offsets first, then asking for the elements they point at:
/// two segment round trips instead of one. Registering every segment before any is fetched is what
/// keeps a scan at one round trip to open and two to the first batch, and it costs extra bytes only
/// for a narrow batch.
/// </remarks>
internal sealed class ListLayoutReader : LayoutReader
{
    private const string Id = "vortex.list";

    private const int ElementsChild = 0;
    private const int OffsetsChild = 1;
    private const int ValidityChild = 2;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ListLayoutReader Instance = new ListLayoutReader();

    private ListLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.List;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.list"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        LayoutNode elements = node.GetChild(ElementsChild);
        LayoutNode offsets = node.GetChild(OffsetsChild);
        FieldMask all = FieldMask.All;

        RegisterChild(in elements, RowRange.FromLength(0, NodeLength(in elements)), in all, segments);

        // One past the last row: `offsets[rows.End]` closes the final list.
        RegisterChild(in offsets, OffsetsRange(rows), in all, segments);

        if (node.ChildCount > ValidityChild)
        {
            LayoutNode validity = node.GetChild(ValidityChild);
            RegisterChild(in validity, rows, in all, segments);
        }
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        ListLayoutMetadata metadata = ListLayoutMetadata.Read(node.Metadata);
        int span = BatchLength(rows);

        LayoutNode elementsLayout = node.GetChild(ElementsChild);
        LayoutNode offsetsLayout = node.GetChild(OffsetsChild);
        FieldMask all = FieldMask.All;

        // The selection is applied here rather than pushed down, because the offsets forbid pushing
        // it: row `r` needs both `offsets[r]` and `offsets[r + 1]`, so a selection of rows is not a
        // selection of offsets - a take of row 5 needs offsets 5 and 6, and the selection vocabulary
        // cannot say that. The children are read over the whole window with the selection suppressed,
        // as `vortex.dict` suppresses it for its values child, and the wanted rows are gathered
        // afterwards.
        // Only the elements child is worth retaining across batches, since it is the one read whole
        // while the offsets child is windowed: without retention a column split into several batches
        // decodes every element once per batch. Same mechanism, key namespace and one-batch exemption
        // as `vortex.dict`'s values child.
        int elementsLength = NodeLength(in elementsLayout);
        bool wholeLayout = rows.Start == 0 && span == NodeLength(in node);
        long elementsKey = ScanContext.LayoutKey(elementsLayout.Index);

        ScanContext.SavedSelection saved = context.ExchangeSelection(null, 0);
        int elementsIndex;
        int offsetsIndex;
        try
        {
            if (wholeLayout)
            {
                elementsIndex = ExecuteChild(
                    in elementsLayout, RowRange.FromLength(0, elementsLength), in all, context);
            }
            else
            {
                if (!context.TryGetRetained(elementsKey, out CanonicalArena held, out int retained))
                {
                    held = context.BeginRetainedDecode();
                    retained = -1;
                    try
                    {
                        retained = ExecuteChild(
                            in elementsLayout, RowRange.FromLength(0, elementsLength), in all, context);
                    }
                    finally
                    {
                        context.EndRetainedDecode(retained);
                    }
                }

                // Records only; the bytes stay in the retained arena.
                elementsIndex = context.Canonical.ReferenceFrom(held, retained);
            }

            offsetsIndex = ExecuteChild(in offsetsLayout, OffsetsRange(rows), in all, context);
        }
        finally
        {
            context.RestoreSelection(in saved);
        }

        CanonicalNode offsets = context.Canonical.GetNode(offsetsIndex);
        if (offsets.Kind != CanonicalKind.Primitive || offsets.PType != metadata.OffsetsPType)
        {
            LayoutsThrow.Format(
                $"A {Id} layout's offsets child decoded as {offsets.Kind}/{offsets.PType.Name()}; " +
                $"a Primitive of {metadata.OffsetsPType.Name()} was declared.");
        }

        if (offsets.Length != span + 1)
        {
            LayoutsThrow.Format(
                $"A {Id} layout's offsets child produced {offsets.Length} rows; {span + 1} were asked for.");
        }

        ReadOnlySpan<int> wanted = context.HasSelection ? context.Selection : default;
        int length = context.HasSelection ? wanted.Length : span;

        Validity validity = Validity.NonNullable;
        if (node.ChildCount > ValidityChild)
        {
            LayoutNode validityLayout = node.GetChild(ValidityChild);
            int validityIndex = ExecuteChild(in validityLayout, rows, in all, context);
            CanonicalNode bits = context.Canonical.GetNode(validityIndex);
            if (bits.Kind != CanonicalKind.Bool)
            {
                LayoutsThrow.Format($"A {Id} layout's validity child decoded to {bits.Kind}, not a Bool.");
            }

            validity = Validity.Bitmap(validityIndex);
        }

        // The canonical ListView wants per-row offsets and sizes, where the layout stores only the
        // `rows + 1` boundaries. Both are materialized here at the offsets' own width, so the
        // elements child is referenced by absolute position and needs no rebasing.
        int width = metadata.OffsetsPType.ByteWidth();
        int bytes = ArrayDecodeContext.CheckedMultiply(length, width, "list offsets");
        VortexBuffer offsetsOut = context.Canonical.Allocate(bytes, width, out Span<byte> starts);
        VortexBuffer sizesOut = context.Canonical.Allocate(bytes, width, out Span<byte> sizes);
        ReadOnlySpan<byte> boundaries = offsets.Values.Span;

        // The selection is in the node's row space and the offsets window starts at the batch's
        // first row, so a wanted row is read at its distance from that row.
        long elementCount = NodeLength(in elementsLayout);
        for (int i = 0; i < length; i++)
        {
            int row = wanted.IsEmpty ? i : wanted[i] - (int)rows.Start;
            if ((uint)row >= (uint)span)
            {
                LayoutsThrow.Format($"A {Id} layout was asked for row {row} of {span}.");
            }

            ulong start = Read(boundaries, row, width);
            ulong end = Read(boundaries, row + 1, width);
            if (end < start || end > (ulong)elementCount)
            {
                LayoutsThrow.Format(
                    $"A {Id} layout's row {row} spans [{start}, {end}) of {elementCount} elements.");
            }

            Write(starts, i, width, start);
            Write(sizes, i, width, end - start);
        }

        int listView = context.Canonical.AddListView(
            node.DType,
            length,
            validity,
            elementsIndex,
            offsetsOut,
            metadata.OffsetsPType,
            sizesOut,
            metadata.OffsetsPType);

        return MaskProjection.Apply(context.Decode, listView, in fields);
    }

    /// <summary>The offsets window: one row longer than the data window, to close the last list.</summary>
    private static RowRange OffsetsRange(RowRange rows) =>
        RowRange.FromLength(rows.Start, BatchLength(rows) + 1);

    private static ulong Read(ReadOnlySpan<byte> values, int index, int width) => width switch
    {
        1 => values[index],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(index * 2, 2)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(values.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(values.Slice(index * 8, 8)),
    };

    private static void Write(Span<byte> values, int index, int width, ulong value)
    {
        switch (width)
        {
            case 1:
                values[index] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(values.Slice(index * 2, 2), (ushort)value);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(values.Slice(index * 4, 4), (uint)value);
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(values.Slice(index * 8, 8), value);
                break;
        }
    }
}
