using System;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// Copies a dtype from one <see cref="DTypeArena"/> into another. An arena refuses children that
/// belong to a foreign arena, and deriving into the source arena instead would mutate a structure
/// concurrent scans are reading, so every derivation imports first and derives second; the target
/// deduplicates, so importing the same dtype again allocates nothing.
/// </summary>
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
