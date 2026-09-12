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
    /// Validates a decoded patch set. <paramref name="indicesNodeIndex"/> and
    /// <paramref name="valuesNodeIndex"/> are the already-decoded children.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="metadata">The descriptor read out of the encoding's own metadata.</param>
    /// <param name="arrayLength">The parent array's row count.</param>
    /// <param name="indicesNodeIndex">The decoded <c>patch_indices</c> child.</param>
    /// <param name="valuesNodeIndex">The decoded <c>patch_values</c> child.</param>
    /// <param name="encodingId">The encoding asking, for the error messages.</param>
    /// <returns>The validated patch set.</returns>
    /// <exception cref="VortexFormatException">Any of the class I rules above fails.</exception>
    public static Patches Create(
        ArrayDecodeContext ctx,
        in PatchesMetadata metadata,
        int arrayLength,
        int indicesNodeIndex,
        int valuesNodeIndex,
        string encodingId)
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

        ulong unsignedOffset = (ulong)offset;
        ulong previous = 0;
        for (int i = 0; i < count; i++)
        {
            ulong index = CompressedValues.ReadUnsigned(indices, indicesPType, i);

            // Sorted ascending: upstream only debug_asserts it, but every lookup path assumes it
            // and the max check below reads only the last entry.
            if (i != 0 && index < previous)
            {
                CompressedThrow.Format(
                    $"{encodingId} patch indices are not sorted: {index} follows {previous}.");
            }

            if (index < unsignedOffset)
            {
                CompressedThrow.Format(
                    $"{encodingId} patch index {index} is below the patch offset {offset}.");
            }

            if (index - unsignedOffset >= (ulong)(uint)arrayLength)
            {
                CompressedThrow.Format(
                    $"{encodingId} patch index {index} at offset {offset} falls outside an array " +
                    $"of {arrayLength} rows.");
            }

            previous = index;
        }

        return Patches.CreateUnchecked(
            indices, indicesPType, count, arrayLength, offset, valuesNodeIndex,
            metadata.HasChunkOffsets);
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
