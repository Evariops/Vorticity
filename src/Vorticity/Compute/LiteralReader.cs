// Reading one row of a canonical column back out as a FilterLiteral.
//
// The pruner needs it, and only the pruner: a zone map's min/max columns hold ordinary values of
// the column's own dtype, and turning them into the same tagged union a filter's constants use is
// what lets ZonePruner compare a bound against a constant with the comparison kernels' own rules --
// signedness and IEEE 754 included -- rather than a second, subtly different set.
//
// A null row reads as "no literal": a zone whose min is null recorded no minimum, which is not the
// same as a minimum of zero and must not be allowed to prune anything.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Reads canonical values back as filter literals.</summary>
internal static class LiteralReader
{
    private const int ViewSize = 16;
    private const int MaxInlineLength = 12;

    /// <summary>
    /// Reads row <paramref name="row"/> of <paramref name="nodeIndex"/> as a literal.
    /// </summary>
    /// <param name="arena">The arena the node lives in.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="row">The row.</param>
    /// <param name="literal">The value, when the row holds one of a comparable type.</param>
    /// <returns><see langword="false"/> for a null row or a type no filter compares.</returns>
    internal static bool TryRead(
        CanonicalArena arena, int nodeIndex, int row, out FilterLiteral literal)
    {
        literal = default;
        int index = ComparisonKernels.Unwrap(arena, nodeIndex);
        CanonicalNode node = arena.GetNode(index);

        if ((uint)row >= (uint)node.Length)
        {
            return false;
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (!mask.IsValid(row))
        {
            return false;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                literal = FilterLiteral.From(
                    CanonicalSupport.BitAt(node.Bits.Span, node.BitOffset + row));
                return true;

            case CanonicalKind.Primitive:
                return TryReadPrimitive(node.PType, node.Values.Span, row, out literal);

            case CanonicalKind.VarBinView:
                literal = FilterLiteral.From(ViewAt(node, row));
                return true;

            case CanonicalKind.Constant:
                // Every row is the element, so the row index is already answered: read the element
                // itself, as row 0 of a one-row values buffer. The validity check above was made
                // against this node, which is where a nullable constant's per-row nulls live.
                switch (node.DType.Kind)
                {
                    case DTypeKind.Primitive:
                        return TryReadPrimitive(node.DType.PType, node.ConstantElement, 0, out literal);
                    case DTypeKind.Utf8:
                    case DTypeKind.Binary:
                        literal = FilterLiteral.From(node.ConstantElement);
                        return true;
                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    /// <summary>Reads one primitive value out of a values buffer, whatever node it came from.</summary>
    /// <remarks>
    /// Takes the BYTES rather than a node so the constant form can hand it an element: a constant
    /// column's element is a one-row values buffer, and building a node to carry it would cost a
    /// record per call for nothing.
    /// </remarks>
    private static bool TryReadPrimitive(
        PType ptype, ReadOnlySpan<byte> bytes, int row, out FilterLiteral literal)
    {
        if (ptype.IsFloat())
        {
            double value = ptype switch
            {
                PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(bytes.Slice(row * 2, 2)),
                PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(row * 4, 4)),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(row * 8, 8)),
            };

            literal = FilterLiteral.From(value);
            return true;
        }

        if (ptype.IsSignedInteger())
        {
            literal = FilterLiteral.From(CanonicalSupport.ReadInteger(bytes, ptype, row));
            return true;
        }

        if (ptype.IsUnsignedInteger())
        {
            literal = FilterLiteral.From(CompressedValues.ReadUnsigned(bytes, ptype, row));
            return true;
        }

        literal = default;
        return false;
    }

    /// <summary>Resolves one 16-byte view, inline or by reference.</summary>
    /// <param name="node">A <c>VarBinView</c> node.</param>
    /// <param name="row">The row.</param>
    internal static ReadOnlySpan<byte> ViewAt(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * ViewSize, ViewSize);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= MaxInlineLength)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer(buffer).Span.Slice(offset, size);
    }
}
