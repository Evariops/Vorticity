using System;
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
        return Core(context, in node, dtype, length, 0, length, ranged: false);
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

    /// <summary>The same range of every field, under the same projection rules as the whole.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, start, count, ranged: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count, bool ranged)
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
            validity = DecodeLeadingValidity(context, in node, length, start, count, ranged);
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
            return Project(context, in node, dtype, length, validity, fieldBase, in projection, start, count, ranged);
        }

        Span<int> stack = stackalloc int[StackFields];
        Scratch<int> fields = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> indices = fields.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                indices[i] = ranged
                    ? context.DecodeChildRange(in node, fieldBase + i, dtype.GetField(i), length, start, count)
                    : context.DecodeChild(in node, fieldBase + i, dtype.GetField(i), length);
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

        int selected = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            if (!projection.Includes(i))
            {
                continue;
            }

            if (!projection.Descend(i).IsAll)
            {
                return false;
            }

            selected++;
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
    /// <param name="start">The first row of the range, when <paramref name="ranged"/>.</param>
    /// <param name="count">The rows produced: the range's, or the whole length.</param>
    /// <param name="ranged">Whether a range of the fields is decoded rather than the whole.</param>
    /// <returns>The canonical struct, holding only those fields.</returns>
    private static int Project(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Validity validity,
        int fieldBase, in Layouts.FieldMask projection, int start, int count, bool ranged)
    {
        int fieldCount = dtype.FieldCount;
        int selected = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            if (projection.Includes(i))
            {
                selected++;
            }
        }

        Scratch<int> children = new Scratch<int>(selected, default);
        Scratch<int> names = new Scratch<int>(selected, default);
        Scratch<DType> types = new Scratch<DType>(selected, default);
        try
        {
            Span<int> childSpan = children.Span;
            Span<int> nameSpan = names.Span;
            Span<DType> typeSpan = types.Span;

            int next = 0;
            for (int i = 0; i < fieldCount; i++)
            {
                if (!projection.Includes(i))
                {
                    continue;
                }

                int child = ranged
                    ? context.DecodeChildRange(in node, fieldBase + i, dtype.GetField(i), length, start, count)
                    : context.DecodeChild(in node, fieldBase + i, dtype.GetField(i), length);
                childSpan[next] = child;
                nameSpan[next] = context.Types.InternName(dtype.GetFieldNameUtf8(i));

                // Imported, not passed through: the field's DType belongs to the file's arena and a
                // struct built here must have children of this one.
                typeSpan[next] = DTypeImport.Into(
                    context.Types, context.Canonical.GetNode(child).DType);
                next++;
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
        ArrayDecodeContext context, in ArrayNode node, int length, int start, int count, bool ranged)
    {
        DType boolType = context.Types.Bool(Nullability.NonNullable);
        int decoded = ranged
            ? context.DecodeChildRange(in node, 0, boolType, length, start, count)
            : context.DecodeChild(in node, 0, boolType, length);

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
