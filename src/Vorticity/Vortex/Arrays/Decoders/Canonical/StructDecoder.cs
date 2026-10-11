using System;
using System.Diagnostics;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.struct</c>: one canonical child per field, with the validity child before
/// them. It is the only encoding that puts validity first, so the shared helper, which is written
/// around a trailing validity child, cannot be used here and the collapse of a constant validity
/// to an all-valid or all-invalid kind is repeated explicitly instead of inherited.
/// </summary>
internal sealed class StructDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.struct";

    private const int StackFields = 32;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly StructDecoder Instance = new StructDecoder();

    /// <summary>Which rows of the children a decode asks for.</summary>
    private enum Rows : byte
    {
        /// <summary>Every row.</summary>
        Whole,

        /// <summary>A contiguous range.</summary>
        Range,

        /// <summary>The rows a selection names.</summary>
        Selected,
    }

    private StructDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.struct"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Struct;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, 0, length, [], Rows.Whole);
    }

    /// <summary>Every child of the node decodes a range, the validity child included.</summary>
    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (int i = 0; i < node.ChildCount; i++)
        {
            if (!context.ChildDecodesRange(in node, i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>No child of the node materializes anything, the validity child included.</summary>
    /// <inheritdoc/>
    public override bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        int fieldCount = dtype.FieldCount;
        int fieldBase = node.ChildCount - fieldCount;
        if (fieldBase is not (0 or 1) || (fieldBase == 1 && !context.ValidityMaterializesNothing(in node, 0)))
        {
            return false;
        }

        for (int i = 0; i < fieldCount; i++)
        {
            if (!context.ChildMaterializesNothing(in node, fieldBase + i, dtype.GetField(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The same range of every field, under the same projection rules as the whole.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count, [], Rows.Range);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <summary>
    /// Every child selects, the validity child included: a child that does not is decoded whole
    /// once by the reader's retained chunk rather than once by every batch of a take.
    /// </summary>
    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        int fieldCount = dtype.FieldCount;
        int fieldBase = node.ChildCount - fieldCount;
        if (fieldBase is not (0 or 1) ||
            (fieldBase == 1 && !context.ChildSelectsWithoutFullDecode(in node, 0, context.Types.Bool(Nullability.NonNullable))))
        {
            return false;
        }

        for (int i = 0; i < fieldCount; i++)
        {
            if (!context.ChildSelectsWithoutFullDecode(in node, fieldBase + i, dtype.GetField(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// What a row costs is its fields' to say, and a field selecting by frame would make a
    /// dictionary over these values pay a frame for each of its entries.
    /// </remarks>
    public override bool SelectsByRow => false;

    /// <summary>
    /// The wanted rows of every field, and of the validity, each selected by its own encoding: no
    /// field is decoded whole to have a few of its rows gathered out of it.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, 0, wanted.Length, wanted, Rows.Selected);
    }

    /// <summary>One child's rows, as <paramref name="rows"/> asks for them.</summary>
    private static int Child(
        ArrayDecodeContext context, in ArrayNode node, int index, DType type, int length, int start, int count,
        ReadOnlySpan<int> wanted, Rows rows) => rows switch
    {
        Rows.Whole => context.DecodeChild(in node, index, type, length),
        Rows.Range => context.DecodeChildRange(in node, index, type, length, start, count),
        Rows.Selected => context.DecodeChildSelected(in node, index, type, length, wanted),
        _ => throw new UnreachableException($"Rows {(byte)rows} is not defined."),
    };

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count,
        ReadOnlySpan<int> wanted, Rows rows)
    {
        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Struct, Id);

        int fieldCount = dtype.FieldCount;
        int childCount = node.ChildCount;

        // nfields -> no validity child; nfields + 1 -> validity at index 0. Nothing else.
        int fieldBase;
        Validity validity;
        if (childCount == fieldCount)
        {
            fieldBase = 0;
            validity = Validity.FromNullability(dtype.Nullability);
        }
        else if (childCount == fieldCount + 1)
        {
            fieldBase = 1;
            validity = DecodeLeadingValidity(context, in node, length, start, count, wanted, rows);
        }
        else
        {
            ArrayDecodeContext.RequireChildCount(childCount, fieldCount, fieldCount + 1, Id);
            return -1;
        }

        // Taken and cleared in one move, so it applies to this struct and to nothing under it: a
        // nested struct, a dictionary's values, a zone map's row of aggregates all decode through
        // here too, and a projection meant for the top level would name their fields by accident.
        // Read before it is taken: reading allocates nothing, and taking would create the holder on
        // every struct decode of every scan to clear something that was never set.
        Layouts.FieldMask projection = context.Scan.PushedFields;
        if (!projection.IsAll)
        {
            context.Scan.ExchangePushedFields(Layouts.FieldMask.All);
        }

        if (Narrows(in projection, fieldCount))
        {
            return Project(context, in node, dtype, length, validity, fieldBase, in projection, start, count, wanted, rows);
        }

        Span<int> stack = stackalloc int[StackFields];
        Scratch<int> fields = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> indices = fields.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                indices[i] = Child(context, in node, fieldBase + i, dtype.GetField(i), length, start, count, wanted, rows);
            }

            return context.Canonical.AddStruct(dtype, count, validity, indices);
        }
        finally
        {
            fields.Dispose();
        }
    }

    /// <summary>
    /// Whether this projection drops whole fields of this struct and narrows nothing deeper.
    /// </summary>
    /// <param name="projection">The pushed projection.</param>
    /// <param name="fieldCount">The struct's field count.</param>
    /// <remarks>
    /// A deeper narrowing is declined rather than honoured: it would need this decode to hand the
    /// sub-mask to a child that has no way to take it, and the caller's own narrowing pass does it
    /// correctly already. What is worth taking here is the common case -- whole columns dropped --
    /// which is the one that decodes fifty and keeps one.
    /// </remarks>
    private static bool Narrows(in Layouts.FieldMask projection, int fieldCount)
    {
        if (projection.IsAll || fieldCount == 0)
        {
            return false;
        }

        int selected = projection.SelectedCount(fieldCount);
        for (int s = 0; s < selected; s++)
        {
            if (!projection.SelectedMask(s).IsAll)
            {
                return false;
            }
        }

        return selected < fieldCount;
    }

    /// <summary>Decodes the fields the projection names and no others.</summary>
    /// <param name="context">Per-batch arenas and the decoder table.</param>
    /// <param name="node">The serialized struct node.</param>
    /// <param name="dtype">The struct's full DType.</param>
    /// <param name="length">The row count.</param>
    /// <param name="validity">The struct's validity, already decoded.</param>
    /// <param name="fieldBase">The serialized index of field zero.</param>
    /// <param name="projection">The fields to keep.</param>
    /// <param name="start">The first row of the range, when <paramref name="rows"/> asks for one.</param>
    /// <param name="count">The rows produced: the range's, the selection's, or the whole length.</param>
    /// <param name="wanted">The rows a selection names, when <paramref name="rows"/> asks for them.</param>
    /// <param name="rows">Which rows of the fields are decoded.</param>
    /// <returns>The canonical struct, holding only those fields.</returns>
    private static int Project(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Validity validity,
        int fieldBase, in Layouts.FieldMask projection, int start, int count, ReadOnlySpan<int> wanted, Rows rows)
    {
        int selected = projection.SelectedCount(dtype.FieldCount);
        Scratch<int> children = new Scratch<int>(selected, default);
        Scratch<int> names = new Scratch<int>(selected, default);
        Scratch<DType> types = new Scratch<DType>(selected, default);
        try
        {
            Span<int> childSpan = children.Span;
            Span<int> nameSpan = names.Span;
            Span<DType> typeSpan = types.Span;

            for (int s = 0; s < selected; s++)
            {
                int i = projection.SelectedField(s);
                int child = Child(context, in node, fieldBase + i, dtype.GetField(i), length, start, count, wanted, rows);
                childSpan[s] = child;
                nameSpan[s] = context.Types.InternName(dtype.GetFieldNameUtf8(i));

                // Imported, not passed through: the field's DType belongs to the file's arena and a
                // struct built here must have children of this one.
                typeSpan[s] = DTypeImport.Into(
                    context.Types, context.Canonical.GetNode(child).DType);
            }

            context.Scan.FieldsHonoured = true;
            DType narrowed = context.Types.Struct(nameSpan, typeSpan, dtype.Nullability);
            return context.Canonical.AddStruct(narrowed, count, validity, childSpan);
        }
        finally
        {
            types.Dispose();
            names.Dispose();
            children.Dispose();
        }
    }

    /// <summary>
    /// The body of <see cref="ArrayDecodeContext.DecodeValidity"/> for a validity child at index 0.
    /// The validity array may itself be encoded, so this goes through the normal dispatch and
    /// never a bitmap fast path.
    /// </summary>
    private static Validity DecodeLeadingValidity(
        ArrayDecodeContext context, in ArrayNode node, int length, int start, int count, ReadOnlySpan<int> wanted, Rows rows)
    {
        DType boolType = context.Types.Bool(Nullability.NonNullable);
        int decoded = Child(context, in node, 0, boolType, length, start, count, wanted, rows);

        CanonicalNode bits = context.Canonical.GetNode(decoded);
        if (bits.Kind != CanonicalKind.Bool)
        {
            throw new VortexFormatException($"A {Id} validity child decoded to {bits.Kind}, not Bool.");
        }

        if (bits.Length != count)
        {
            throw new VortexFormatException(
                $"A {Id} validity child of {bits.Length} rows cannot describe an array of {count}.");
        }

        if (count == 0)
        {
            return Validity.AllValid;
        }

        return ArrayDecodeContext.ClassifyValidityBits(bits.Bits.Span, bits.BitOffset, count) switch
        {
            ValidityBitmapShape.AllClear => Validity.AllInvalid,
            ValidityBitmapShape.AllSet => Validity.AllValid,
            _ => Validity.Bitmap(decoded),
        };
    }
}
