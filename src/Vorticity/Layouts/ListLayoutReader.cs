// vortex.list - vortex-layout-0.86.1/src/layouts/list/{mod.rs,reader.rs}.
//
// A list column shredded into separate children rather than held as one array: elements, offsets,
// and - when the dtype is nullable - a validity child. The offsets child carries `rows + 1` entries,
// so element `i` of row `r` lives at `elements[offsets[r] + i]`.
//
// ONE PASS, AND THE WHOLE ELEMENTS CHILD. Reading rows [a, b) selectively would mean fetching the
// offsets, reading `offsets[a]` and `offsets[b]`, and only then knowing which elements to ask for -
// two segment round trips, which is what upstream's reader does with futures. This architecture
// registers every segment it needs BEFORE any is fetched, and `RoundTripCountTests` holds that
// property at exactly one round trip to open and two to the first batch. So the elements child is
// registered whole and the window is taken afterwards.
//
// That is a real trade and not an oversight: it reads more bytes than upstream for a narrow batch,
// and it keeps the round-trip count that docs/01 §3 makes a headline promise. The array-level
// `vortex.listview` already behaves this way - its elements child is read whole - so the layout is
// not adding a behaviour, only declining to remove one.
using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.list</c> layout: elements, offsets and optional validity children.</summary>
public sealed class ListLayoutReader : LayoutReader
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

        // THE SELECTION IS APPLIED HERE, NOT PUSHED DOWN, and the offsets are why. Row `r` needs
        // BOTH `offsets[r]` and `offsets[r + 1]`, so a selection of rows is not a selection of
        // offsets - a take of row 5 needs offsets 5 and 6, and the selection vocabulary cannot say
        // that. The children are therefore read over the whole window with the selection suppressed,
        // exactly as `vortex.dict` suppresses it for its values child, and the wanted rows are
        // gathered afterwards.
        (int[]? Buffer, int Count) saved = context.ExchangeSelection(null, 0);
        int elementsIndex;
        int offsetsIndex;
        try
        {
            elementsIndex = ExecuteChild(
                in elementsLayout, RowRange.FromLength(0, NodeLength(in elementsLayout)), in all, context);
            offsetsIndex = ExecuteChild(in offsetsLayout, OffsetsRange(rows), in all, context);
        }
        finally
        {
            context.ExchangeSelection(saved.Buffer, saved.Count);
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

        // The canonical ListView wants per-row offsets AND sizes, where the layout stores only the
        // `rows + 1` boundaries. Both are materialized here at the offsets' own width, so the
        // elements child is referenced by absolute position and needs no rebasing.
        int width = metadata.OffsetsPType.ByteWidth();
        VortexBuffer offsetsOut = context.Canonical.Allocate(length * width, width, out Span<byte> starts);
        VortexBuffer sizesOut = context.Canonical.Allocate(length * width, width, out Span<byte> sizes);
        ReadOnlySpan<byte> boundaries = offsets.Values.Span;

        long elementCount = NodeLength(in elementsLayout);
        for (int i = 0; i < length; i++)
        {
            int row = wanted.IsEmpty ? i : wanted[i];
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
