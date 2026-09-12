// vortex.masked - vortex-array-0.86.1/src/arrays/masked/vtable/mod.rs `deserialize`.
//
// Child 0 is the values at `P` with nullability STRIPPED; the optional child 1 is the mask. `P`
// itself must be nullable - `MaskedData::try_new` rejects a non-nullable dtype, and with only one
// child the validity is `Validity::from(dtype.nullability())`, which for a non-nullable dtype
// would make the whole encoding a no-op.
//
// Upstream additionally asserts the child contains no nulls (`child.all_valid(...)`). That is an
// O(n) scan of information the mask already supersedes, so docs/08-semantics.md §5 puts it in
// class II: it runs only under VortexReadOptions.VerifyStatistics.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.masked</c>: a non-nullable child plus a null mask.</summary>
public sealed class MaskedDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.masked";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly MaskedDecoder Instance = new MaskedDecoder();

    private MaskedDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.masked"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Masked;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, 2, Id);

        if (dtype.IsDefault || dtype.Nullability != Nullability.Nullable)
        {
            throw new VortexFormatException(
                $"{Id} produces a nullable array; it was asked for a non-nullable dtype.");
        }

        // IMPORT FIRST, THEN FLIP. `dtype` is the layout node's own dtype, which is a handle into
        // the FILE's arena - LayoutParser stores the schema's nodes verbatim and FlatLayoutReader
        // hands them straight to DecodeRoot. `WithNullability` on a non-leaf node calls
        // DTypeArena.CloneWithNullability, which grows arrays, bumps _nodeCount and rehashes the
        // dedup table with no synchronization; doing that to the file's arena would race every
        // other scan of the same open file, which docs/09-contracts.md §1 says must be safe.
        // context.Types is this flow's own arena, and each parallel split has its own.
        DType childDType = DTypeImport.Into(context.Types, dtype).WithNullability(Nullability.NonNullable);
        int childIndex = context.DecodeChild(in node, 0, childDType, length);
        Validity validity = context.DecodeValidity(in node, 1, dtype.Nullability, length);

        if (context.Options.VerifyStatistics)
        {
            VerifyChildHasNoNulls(context, childIndex);
        }

        return CanonicalRewrap.WithValidity(context, childIndex, dtype, validity, length);
    }

    /// <summary>
    /// Class II (docs/08-semantics.md §5): upstream's <c>child.all_valid()</c> assertion, run only
    /// when the caller has asked for statistics to be verified.
    /// </summary>
    private static void VerifyChildHasNoNulls(ArrayDecodeContext context, int childIndex)
    {
        CanonicalNode child = context.Canonical.GetNode(childIndex);
        if (child.Validity.IsAllValid)
        {
            return;
        }

        throw new VortexFormatException(
            $"{Id}'s child must contain no nulls; it reports {child.Validity.Kind}.");
    }
}
