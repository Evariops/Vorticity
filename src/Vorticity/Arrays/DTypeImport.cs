// Copying a DType from one DTypeArena into another.
//
// WHY THIS EXISTS. `DTypeArena.Struct` rejects children from a foreign arena ("Child DTypes must
// come from the same DTypeArena as their parent"), and several dtypes this library DERIVES are
// built over a column dtype that lives somewhere else:
//
//   * the zoned/stats zones table, built at parse time over the file schema's nodes;
//   * the projected struct a FieldMask produces at scan time, built in the ScanContext's own arena
//     over field dtypes that came from the layout tree;
//   * vortex.masked's non-nullable child dtype, flipped from the node's own dtype at decode time.
//
// Deriving into the file's arena instead would work but would MUTATE a DTypeArena that concurrent
// scans are reading - `WithNullability` on a non-leaf node calls `DTypeArena.CloneWithNullability`,
// which grows arrays and rehashes the dedup table with no synchronization. That is why ScanContext
// keeps its own arena (contract §8.3) and why every derivation imports first and derives second.
// Copying is cheap: the target arena deduplicates, so the second batch's import finds every node
// already there and allocates nothing.
//
// WHY THE WALK IS MEMOISED. A DTypeArena deduplicates, so `Struct(["a","b"], [d, d])` stores one
// child index twice and a dtype nested that way 64 deep is a 65-node DAG with 2^64 root-to-leaf
// paths: the depth cap bounds the stack, not the work (docs/03-architecture.md §6). A flat visit
// budget bounds that, but it also rejects perfectly ordinary breadth - a 5000-field struct of
// identically-typed columns is FOUR distinct nodes in the source arena and 5001 visits - so the
// budget is only the allocation-free fast path here. Cross it and the walk switches to a memo
// keyed on the source node index, exactly as DTypeFlatBuffers.ReadTableCore does, and the work
// becomes linear in the source's DISTINCT node count. Nothing is rejected: any dtype the source
// arena could build, this can copy.
//
// This lives in Vorticity.Arrays rather than Vorticity.Layouts because both Layouts and the
// canonical decoders need it, and Layouts already depends on Arrays.
using System;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Arrays;

internal static class DTypeImport
{
    /// <summary>How many dtype nodes one import may visit before it starts memoising.</summary>
    private const int VisitBudget = 4096;

    /// <summary>Returns <paramref name="source"/> as a node of <paramref name="target"/>.</summary>
    /// <param name="target">The arena to build in.</param>
    /// <param name="source">The dtype to copy.</param>
    /// <returns><paramref name="source"/> itself when it already belongs to <paramref name="target"/>.</returns>
    /// <exception cref="VortexFormatException"><paramref name="source"/> has not been read yet.</exception>
    internal static DType Into(DTypeArena target, DType source)
    {
        if (source.IsDefault)
        {
            ArraysThrow.Format("A dtype cannot be imported before it has been read.");
        }

        if (ReferenceEquals(source.Arena, target))
        {
            return source;
        }

        ImportWalk walk = new ImportWalk { Budget = VisitBudget };
        return Copy(target, source, ref walk, depth: 1);
    }

    private static DType Copy(DTypeArena target, DType source, ref ImportWalk walk, int depth)
    {
        // A memo hit skips the depth check on purpose: nothing recurses below it, and the target
        // arena recomputes each node's depth from its children and applies VortexLimits at
        // construction, so a too-deep dtype is still rejected - by the arena, not by the walk.
        if (walk.Memo is not null && walk.Memo.TryGetValue(source.NodeIndex, out DType cached))
        {
            return cached;
        }

        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "DType");

        // The budget is a fast path, not a cap: crossing it buys a dictionary, not an exception.
        if (walk.Memo is null && --walk.Budget < 0)
        {
            walk.Memo = new Dictionary<int, DType>();
        }

        DType result = CopyCore(target, source, ref walk, depth);

        // Re-read: the recursion above may have switched the walk into memoising mode.
        if (walk.Memo is not null)
        {
            walk.Memo[source.NodeIndex] = result;
        }

        return result;
    }

    private static DType CopyCore(DTypeArena target, DType source, ref ImportWalk walk, int depth)
    {
        Nullability nullability = source.Nullability;
        switch (source.Kind)
        {
            case DTypeKind.Null:
                return target.Null(nullability);

            case DTypeKind.Bool:
                return target.Bool(nullability);

            case DTypeKind.Primitive:
                return target.Primitive(source.PType, nullability);

            case DTypeKind.Decimal:
                return target.Decimal(source.Precision, source.Scale, nullability);

            case DTypeKind.Utf8:
                return target.Utf8(nullability);

            case DTypeKind.Binary:
                return target.Binary(nullability);

            case DTypeKind.Variant:
                return target.Variant(nullability);

            case DTypeKind.List:
                return target.List(Copy(target, source.ElementType, ref walk, depth + 1), nullability);

            case DTypeKind.FixedSizeList:
                return target.FixedSizeList(
                    Copy(target, source.ElementType, ref walk, depth + 1), source.FixedSize, nullability);

            case DTypeKind.Extension:
                return target.Extension(
                    source.ExtensionIdUtf8,
                    Copy(target, source.StorageType, ref walk, depth + 1),
                    source.ExtensionMetadata);

            case DTypeKind.Map:
                return target.Map(
                    Copy(target, source.KeyType, ref walk, depth + 1),
                    Copy(target, source.ValueType, ref walk, depth + 1),
                    source.KeysSorted,
                    nullability);

            case DTypeKind.Struct:
                return CopyStruct(target, source, ref walk, depth, union: false);

            default:
                return CopyStruct(target, source, ref walk, depth, union: true);
        }
    }

    private static DType CopyStruct(DTypeArena target, DType source, ref ImportWalk walk, int depth, bool union)
    {
        int count = source.FieldCount;

        Scratch<DType> fields = new Scratch<DType>(count, default);
        Scratch<int> names = new Scratch<int>(count, default);
        Scratch<byte> typeIds = new Scratch<byte>(union ? count : 0, default);
        try
        {
            Span<DType> fieldSpan = fields.Span;
            Span<int> nameSpan = names.Span;
            for (int i = 0; i < count; i++)
            {
                fieldSpan[i] = Copy(target, source.GetField(i), ref walk, depth + 1);
                nameSpan[i] = target.InternName(source.GetFieldNameUtf8(i));
            }

            if (!union)
            {
                return target.Struct(nameSpan, fieldSpan, source.Nullability);
            }

            Span<byte> typeIdSpan = typeIds.Span;
            for (int i = 0; i < count; i++)
            {
                typeIdSpan[i] = source.GetTypeId(i);
            }

            return target.Union(nameSpan, fieldSpan, typeIdSpan, source.Nullability);
        }
        finally
        {
            typeIds.Dispose();
            names.Dispose();
            fields.Dispose();
        }
    }

    /// <summary>
    /// One import's mutable state: the remaining allocation-free visits, and - once those run out -
    /// the source-node-index memo that makes the rest of the walk linear.
    /// </summary>
    private struct ImportWalk
    {
        public int Budget;
        public Dictionary<int, DType>? Memo;
    }
}
