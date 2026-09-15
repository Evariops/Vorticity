// The shared patch structure - Phase 1 contract §10.4, vortex-array-0.86.1/src/patches.rs and
// spec/METADATA.md "Shared". ONE implementation, used by fastlanes.bitpacked and vortex.sparse
// today and by ALP / ALPrd in Phase 2.
//
// Upstream's Patches::new performs some of these checks and only `debug_assert`s the sortedness.
// We are reading untrusted input, so every one of them is a hard check here:
//   * indices.len() == values.len(), and neither is empty;
//   * indices are non-nullable unsigned;
//   * indices.len() <= array_len;
//   * indices are non-decreasing;
//   * every index is >= offset and index - offset < array_len, computed without wrapping.
// Upstream computes `max - offset` as a usize subtraction, so a first index below the offset
// underflows there; here it is rejected.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Patch indices, patch values and the optional per-chunk index offsets, validated.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> because it borrows the decoded indices out of the canonical arena: a
/// <see cref="Patches"/> is meaningful only inside the decode call that built it (contract §2.2
/// rules 4 and 5).
/// </remarks>
public readonly ref struct Patches
{
    /// <summary>
    /// <c>PATCH_CHUNK_SIZE</c>: one index offset is stored per chunk of this many rows, which is
    /// what makes patch lookup constant time.
    /// </summary>
    public const int ChunkSize = PatchesMetadata.ChunkSize;

    private readonly ReadOnlySpan<byte> _indices;
    private readonly PType _indicesPType;
    private readonly int _count;
    private readonly int _arrayLength;
    private readonly int _offset;
    private readonly int _valuesNodeIndex;
    private readonly bool _hasChunkOffsets;

    private Patches(
        ReadOnlySpan<byte> indices,
        PType indicesPType,
        int count,
        int arrayLength,
        int offset,
        int valuesNodeIndex,
        bool hasChunkOffsets)
    {
        _indices = indices;
        _indicesPType = indicesPType;
        _count = count;
        _arrayLength = arrayLength;
        _offset = offset;
        _valuesNodeIndex = valuesNodeIndex;
        _hasChunkOffsets = hasChunkOffsets;
    }

    /// <summary>How many patches there are. Always at least one.</summary>
    public int Count => _count;

    /// <summary>The length of the array being patched.</summary>
    public int ArrayLength => _arrayLength;

    /// <summary>The row offset the stored indices are relative to.</summary>
    public int Offset => _offset;

    /// <summary>The canonical node holding the patch values.</summary>
    public int ValuesNodeIndex => _valuesNodeIndex;

    /// <summary>Whether the descriptor declared a chunk-offsets child.</summary>
    public bool HasChunkOffsets => _hasChunkOffsets;

    /// <summary>
    /// The position patch <paramref name="i"/> occupies in the parent array:
    /// <c>indices[i] - Offset</c>, validated at construction to lie in
    /// <c>[0, <see cref="ArrayLength"/>)</c>.
    /// </summary>
    /// <param name="i">A patch, in <c>[0, <see cref="Count"/>)</c>.</param>
    /// <exception cref="VortexFormatException"><paramref name="i"/> is out of range.</exception>
    public int GetPosition(int i)
    {
        if ((uint)i >= (uint)_count)
        {
            return CompressedThrow.Format<int>($"Patch index {i} is outside [0, {_count}).");
        }

        return (int)(CompressedValues.ReadUnsigned(_indices, _indicesPType, i) - (ulong)_offset);
    }

    /// <summary>
    /// Scatters every patch value into <paramref name="destination"/> at its own position.
    /// </summary>
    /// <param name="values">The patch values, one per patch, at <paramref name="width"/> bytes.</param>
    /// <param name="width">Bytes per value.</param>
    /// <param name="destination">The array being patched, <see cref="ArrayLength"/> rows.</param>
    /// <remarks>
    /// The index type and the value width are properties of the node, and the loop this replaces
    /// asked about both per PATCH: `GetPosition` through `ReadUnsigned`'s switch, then a
    /// variable-width `Slice(..).CopyTo(..)`. On `vortex.alp` the patch set is large enough that
    /// applying it was 37% of the scan.
    /// </remarks>
    public readonly void ApplyAll(ReadOnlySpan<byte> values, int width, Span<byte> destination)
    {
        switch (_indicesPType)
        {
            case PType.U8:
                ApplyAll<byte>(values, width, destination);
                break;
            case PType.U16:
                ApplyAll<ushort>(values, width, destination);
                break;
            case PType.U32:
                ApplyAll<uint>(values, width, destination);
                break;
            default:
                ApplyAll<ulong>(values, width, destination);
                break;
        }
    }

    private readonly void ApplyAll<TIndex>(
        ReadOnlySpan<byte> values, int width, Span<byte> destination)
        where TIndex : unmanaged
    {
        ReadOnlySpan<TIndex> indices = MemoryMarshal.Cast<byte, TIndex>(_indices)[.._count];
        switch (width)
        {
            case 1:
                Scatter<TIndex, byte>(indices, values, destination);
                break;
            case 2:
                Scatter<TIndex, ushort>(indices, values, destination);
                break;
            case 4:
                Scatter<TIndex, uint>(indices, values, destination);
                break;
            case 8:
                Scatter<TIndex, ulong>(indices, values, destination);
                break;
            default:
                for (int i = 0; i < _count; i++)
                {
                    int position = (int)(Widen(indices[i]) - (ulong)_offset);
                    values.Slice(i * width, width)
                        .CopyTo(destination.Slice(position * width, width));
                }

                break;
        }
    }

    private readonly void Scatter<TIndex, TValue>(
        ReadOnlySpan<TIndex> indices, ReadOnlySpan<byte> values, Span<byte> destination)
        where TIndex : unmanaged
        where TValue : unmanaged
    {
        ReadOnlySpan<TValue> source = MemoryMarshal.Cast<byte, TValue>(values)[..indices.Length];
        Span<TValue> target = MemoryMarshal.Cast<byte, TValue>(destination)[.._arrayLength];
        ulong offset = (ulong)_offset;
        for (int i = 0; i < indices.Length; i++)
        {
            // Validated at construction: every index ascends, is at or above the offset, and the
            // last one is inside the array -- so every position is in [0, ArrayLength).
            target[(int)(Widen(indices[i]) - offset)] = source[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Widen<TIndex>(TIndex index)
        where TIndex : unmanaged
    {
        if (typeof(TIndex) == typeof(byte))
        {
            return Unsafe.As<TIndex, byte>(ref index);
        }

        if (typeof(TIndex) == typeof(ushort))
        {
            return Unsafe.As<TIndex, ushort>(ref index);
        }

        if (typeof(TIndex) == typeof(uint))
        {
            return Unsafe.As<TIndex, uint>(ref index);
        }

        return Unsafe.As<TIndex, ulong>(ref index);
    }

    /// <summary>
    /// Overwrites the patched rows that survived a selection, at the positions they now occupy.
    /// </summary>
    /// <param name="patches">The validated patch set, over the FULL row space.</param>
    /// <param name="values">The patch values, one per patch, at <paramref name="width"/> bytes.</param>
    /// <param name="width">Bytes per value.</param>
    /// <param name="wanted">The selected rows, strictly ascending.</param>
    /// <param name="destination">The selected rows' values, in selection order.</param>
    /// <remarks>
    /// <para>
    /// A MERGE, not a search per patch: both sides are ascending, so one walk over each is enough.
    /// The patch set is left whole rather than selected into - ~~there are few patches by
    /// construction~~ - and pushing a second selection into their own child arrays would mean
    /// re-basing indices that are expressed in the parent's row space.
    /// </para>
    /// <para>
    /// A MERGE WALKS THE LONGER LIST, AND THAT IS THE WHOLE COST HERE. "Few patches by construction"
    /// is true of a ratio and false of a count: the 1M-row ALP axis carries 16 454 of them, and
    /// `FlatLayoutReader` serves a take one batch at a time, so this ran 16 454 iterations to place
    /// AT MOST ONE row, on every batch. Short-circuiting it took that take from 12 198 µs to 244 -
    /// `14.97` to `0.40`, 98% of everything left after v2 R26.
    /// </para>
    /// <para>
    /// SO THE SHORTER LIST DRIVES. When there are fewer wanted rows than patches, each wanted row
    /// binary-searches the patches from where the last one landed: O(wanted . log patches) instead
    /// of O(patches). When there are more, the original merge is already the right shape and is
    /// kept. The test is the two lengths against each other - no threshold, no tuning.
    /// </para>
    /// <para>
    /// THE SEARCH RELIES ON `RequireAscending`, which <see cref="Create"/> runs. Since v2 R26 that
    /// walk may be SKIPPED on later batches of a node it has already passed for - skipped because
    /// the answer is known, never because it was not asked - so the ordering this search needs holds
    /// either way.
    /// </para>
    /// </remarks>
    public static void ApplySelected(
        in Patches patches, ReadOnlySpan<byte> values, int width, ReadOnlySpan<int> wanted,
        Span<byte> destination)
    {
        if (wanted.Length < patches.Count)
        {
            int from = 0;
            for (int w = 0; w < wanted.Length; w++)
            {
                int found = Find(in patches, wanted[w], from);
                if (found < 0)
                {
                    // `Find` returns the insertion point negated, which is where the NEXT wanted
                    // row's search may start: both lists ascend, so nothing before it can match.
                    from = ~found;
                    if (from >= patches.Count)
                    {
                        return;
                    }

                    continue;
                }

                values.Slice(found * width, width).CopyTo(destination.Slice(w * width, width));
                from = found + 1;
                if (from >= patches.Count)
                {
                    return;
                }
            }

            return;
        }

        int at = 0;
        for (int i = 0; i < patches.Count && at < wanted.Length; i++)
        {
            int position = patches.GetPosition(i);
            while (at < wanted.Length && wanted[at] < position)
            {
                at++;
            }

            if (at < wanted.Length && wanted[at] == position)
            {
                values.Slice(i * width, width).CopyTo(destination.Slice(at * width, width));
            }
        }
    }

    /// <summary>
    /// The patch at <paramref name="row"/>, searched in <c>[from, Count)</c>, or the bitwise
    /// complement of the first patch beyond it when there is none.
    /// </summary>
    /// <param name="patches">The patch set, whose positions ascend.</param>
    /// <param name="row">The row to find.</param>
    /// <param name="from">The first patch that may still match.</param>
    private static int Find(in Patches patches, int row, int from)
    {
        int low = from;
        int high = patches.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int position = patches.GetPosition(middle);
            if (position == row)
            {
                return middle;
            }

            if (position < row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>
    /// Validates a decoded patch set. <paramref name="indicesNodeIndex"/> and
    /// <paramref name="valuesNodeIndex"/> are the already-decoded children.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="metadata">The descriptor read out of the encoding's own metadata.</param>
    /// <param name="arrayLength">The parent array's row count.</param>
    /// <param name="indicesNodeIndex">The decoded <c>patch_indices</c> child.</param>
    /// <param name="valuesNodeIndex">The decoded <c>patch_values</c> child.</param>
    /// <param name="encodingId">The encoding asking, for the error messages.</param>
    /// <param name="indicesAlreadyChecked">
    /// <see langword="true"/> when THIS scan has already walked these indices and found them
    /// ascending, so the O(n) walk may be skipped. Everything else here is O(1) and always runs.
    /// </param>
    /// <returns>The validated patch set.</returns>
    /// <remarks>
    /// THE ONE O(n) STEP IS THE MONOTONICITY WALK, and on a take it was re-run for every batch of
    /// the same node - 16 454 patch indices per call on the 1M-row ALP axis. Ascending is a property
    /// of the node's bytes, not of the batch, so the caller may answer it once per scan through
    /// <see cref="ArrayDecodeContext.IsNodeChecked"/>; outside a take on an oversized node that
    /// method answers false and this walks exactly as before (v2 R26).
    /// </remarks>
    public static Patches Create(
        ArrayDecodeContext ctx,
        in PatchesMetadata metadata,
        int arrayLength,
        int indicesNodeIndex,
        int valuesNodeIndex,
        string encodingId,
        bool indicesAlreadyChecked = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        int count = ArrayDecodeContext.CheckedLength(metadata.Length, $"{encodingId} patch count");
        int offset = ArrayDecodeContext.CheckedLength(metadata.Offset, $"{encodingId} patch offset");

        // `Patch indices must not be empty` - an empty patch set is malformed upstream, which is
        // why encodings/sparse_r0 and sparse_r1 are in the corpus manifest's `skipped` list.
        if (count == 0)
        {
            CompressedThrow.Format($"{encodingId} declares an empty patch set, which is malformed.");
        }

        if (count > arrayLength)
        {
            CompressedThrow.Format(
                $"{encodingId} declares {count} patches over an array of {arrayLength} rows.");
        }

        // PatchesMetadata.ReadBody already rejects a signed indices ptype; assert the domain here
        // too, because this is the check that keeps GetPosition in bounds.
        PType indicesPType = metadata.IndicesPType;
        if (!indicesPType.IsUnsignedInteger())
        {
            CompressedThrow.Format(
                $"{encodingId} patch indices are {indicesPType.Name()}; they must be unsigned.");
        }

        ReadOnlySpan<byte> indices = CompressedValues.RequireIndexChild(
            ctx, indicesNodeIndex, indicesPType, count, encodingId, "patch_indices");

        CanonicalNode values = ctx.Canonical.GetNode(valuesNodeIndex);
        if (values.Length != count)
        {
            CompressedThrow.ChildLength(encodingId, "patch_values", values.Length, count);
        }

        // SORTED FIRST, THEN THE TWO ENDS. The original loop asked three questions of every index
        // through `ReadUnsigned`'s switch; two of the three are implied by the first once the
        // sequence is known to ascend. If indices[0] >= offset then every index is, and if the LAST
        // index is inside the array then every index is -- so the per-index work is a monotonicity
        // check, which is a shifted compare and belongs to the vector unit.
        ulong unsignedOffset = (ulong)offset;
        if (!indicesAlreadyChecked)
        {
            RequireAscending(indices, indicesPType, count, encodingId);
        }

        ulong first = CompressedValues.ReadUnsigned(indices, indicesPType, 0);
        if (first < unsignedOffset)
        {
            CompressedThrow.Format(
                $"{encodingId} patch index {first} is below the patch offset {offset}.");
        }

        ulong last = CompressedValues.ReadUnsigned(indices, indicesPType, count - 1);
        if (last - unsignedOffset >= (ulong)(uint)arrayLength)
        {
            CompressedThrow.Format(
                $"{encodingId} patch index {last} at offset {offset} falls outside an array " +
                $"of {arrayLength} rows.");
        }

        return Patches.CreateUnchecked(
            indices, indicesPType, count, arrayLength, offset, valuesNodeIndex,
            metadata.HasChunkOffsets);
    }

    /// <summary>
    /// Class I: the patch indices must ascend. Upstream only <c>debug_assert</c>s it, but every
    /// lookup path assumes it and the endpoint checks above rely on it.
    /// </summary>
    private static void RequireAscending(
        ReadOnlySpan<byte> indices, PType ptype, int count, string encodingId)
    {
        switch (ptype)
        {
            case PType.U8:
                RequireAscending<byte>(indices, count, encodingId);
                break;
            case PType.U16:
                RequireAscending<ushort>(indices, count, encodingId);
                break;
            case PType.U32:
                RequireAscending<uint>(indices, count, encodingId);
                break;
            default:
                RequireAscending<ulong>(indices, count, encodingId);
                break;
        }
    }

    private static void RequireAscending<T>(ReadOnlySpan<byte> indices, int count, string encodingId)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> typed = MemoryMarshal.Cast<byte, T>(indices)[..count];

        int i = 1;
        if (Vector.IsHardwareAccelerated && count > Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            for (; i <= count - lanes; i += lanes)
            {
                if (Vector.LessThanAny(Vector.LoadUnsafe(in typed[i]), Vector.LoadUnsafe(in typed[i - 1])))
                {
                    // One pair in this block descends; the scalar loop below names which.
                    break;
                }
            }
        }

        for (; i < count; i++)
        {
            if (typed[i] < typed[i - 1])
            {
                CompressedThrow.Format(
                    $"{encodingId} patch indices are not sorted: {typed[i]} follows {typed[i - 1]}.");
            }
        }
    }

    private static Patches CreateUnchecked(
        ReadOnlySpan<byte> indices,
        PType indicesPType,
        int count,
        int arrayLength,
        int offset,
        int valuesNodeIndex,
        bool hasChunkOffsets) =>
        new(indices, indicesPType, count, arrayLength, offset, valuesNodeIndex, hasChunkOffsets);
}
