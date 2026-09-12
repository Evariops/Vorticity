// vortex.struct - vortex-layout-0.86.1/src/layouts/struct_/mod.rs. Zero segments, empty metadata,
// one child per field plus a leading validity child when the struct dtype is nullable:
//
//     slot_to_child(0) = nullable.then_some(0)
//     slot_to_child(s) = s - 1 + nullable          so field k sits at serialized index k + nullable
//     slot_dtype(0)    = Bool(NonNullable)         NOT nullable Bool
//
// THIS IS THE LAZY-RESOLUTION PATH (docs/08-semantics.md §4). A field the FieldMask excludes is
// neither registered nor executed, so an unknown layout or an unknown array encoding buried in an
// unprojected column never throws.
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.struct</c> layout: one child layout per field, plus validity.</summary>
public sealed class StructLayoutReader : LayoutReader
{
    private const string Id = "vortex.struct";
    private const int StackFields = 16;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly StructLayoutReader Instance = new StructLayoutReader();

    private StructLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Struct;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.struct"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        DType dtype = RequireStruct(in node);
        int validityChildren = dtype.IsNullable ? 1 : 0;
        if (validityChildren == 1)
        {
            // The struct's own row validity is needed whatever the projection selects.
            LayoutNode validity = node.GetChild(0);
            FieldMask all = FieldMask.All;
            RegisterChild(in validity, rows, in all, segments);
        }

        int fieldCount = dtype.FieldCount;
        for (int k = 0; k < fieldCount; k++)
        {
            if (!fields.Includes(k))
            {
                continue;
            }

            LayoutNode child = node.GetChild(k + validityChildren);
            FieldMask childMask = fields.Descend(k);
            RegisterChild(in child, rows, in childMask, segments);
        }
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        DType dtype = RequireStruct(in node);

        // With a selection in force every child produces the SELECTED count, so that is the struct's
        // length too. Getting this from the range instead would build a struct whose declared length
        // disagreed with its fields'.
        int length = context.HasSelection ? context.Selection.Length : BatchLength(rows);
        int validityChildren = dtype.IsNullable ? 1 : 0;
        int fieldCount = dtype.FieldCount;

        int selected = 0;
        for (int k = 0; k < fieldCount; k++)
        {
            if (fields.Includes(k))
            {
                selected++;
            }
        }

        Validity validity = Validity.FromNullability(dtype.Nullability);
        if (validityChildren == 1)
        {
            LayoutNode validityChild = node.GetChild(0);
            FieldMask all = FieldMask.All;
            int decoded = ExecuteChild(in validityChild, rows, in all, context);
            validity = LayoutValidity.FromChild(context, decoded, length, Id);
        }

        // A whole projection keeps the node's own dtype - which is also the only way an
        // unprojected read reproduces the file's schema exactly - so it needs no field names and no
        // dtype import at all.
        bool whole = fields.IsAll;

        Span<int> stack = stackalloc int[StackFields];
        Scratch<int> children = new Scratch<int>(selected, stack);

        // A DType is managed and cannot be stackalloc'd, so the name and dtype scratch come from
        // the pool. Both are returned in the finally.
        Scratch<int> names = new Scratch<int>(whole ? 0 : selected, default);
        Scratch<DType> fieldTypes = new Scratch<DType>(whole ? 0 : selected, default);
        try
        {
            Span<int> childSpan = children.Span;
            Span<int> nameSpan = names.Span;
            Span<DType> typeSpan = fieldTypes.Span;

            int next = 0;
            for (int k = 0; k < fieldCount; k++)
            {
                if (!fields.Includes(k))
                {
                    continue;
                }

                LayoutNode child = node.GetChild(k + validityChildren);
                FieldMask childMask = fields.Descend(k);
                int decoded = ExecuteChild(in child, rows, in childMask, context);

                childSpan[next] = decoded;
                if (!whole)
                {
                    nameSpan[next] = context.Types.InternName(dtype.GetFieldNameUtf8(k));
                    typeSpan[next] = DTypeImport.Into(context.Types, context.Canonical.GetNode(decoded).DType);
                }

                next++;
            }

            DType produced = whole ? dtype : context.Types.Struct(nameSpan, typeSpan, dtype.Nullability);
            return context.Canonical.AddStruct(produced, length, validity, childSpan);
        }
        finally
        {
            fieldTypes.Dispose();
            names.Dispose();
            children.Dispose();
        }
    }

    private static DType RequireStruct(in LayoutNode node)
    {
        DType dtype = node.DType;
        if (dtype.Kind != DTypeKind.Struct)
        {
            LayoutsThrow.Format($"A {Id} layout cannot produce the non-struct dtype {dtype}.");
        }

        return dtype;
    }
}
