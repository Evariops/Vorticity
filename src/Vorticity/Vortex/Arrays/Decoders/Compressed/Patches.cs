using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Patch indices, patch values and the optional per-chunk index offsets, validated. The single
/// patch structure the compressed encodings share; the reference implementation only asserts the
/// sortedness in debug builds, but these bytes are untrusted, so every invariant a lookup relies
/// on is a hard check here.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> because it borrows the decoded indices out of the canonical arena: a
/// <see cref="Patches"/> is meaningful only inside the decode call that built it.
/// </remarks>
internal readonly ref struct Patches
{
    /// <summary>
    /// One index offset is stored per chunk of this many rows, which is what makes patch lookup
    /// constant time.
    /// </summary>
    public const int ChunkSize = PatchesMetadata.ChunkSize;

    /// <summary>
    /// The fewest patches a selective read decodes once for every read of its node: a smaller set
    /// costs less to decode again at each read than to retain.
    /// </summary>
    public const int RetainedFrom = 4_096;

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

    /// <summary>
    /// The position patch <paramref name="i"/> occupies in the parent array:
    /// <c>indices[i] - Offset</c>, validated at construction to lie in
    /// <c>[0, the parent array's length)</c>.
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
    /// <param name="destination">The array being patched, every row of the parent array.</param>
    /// <remarks>
    /// The index type and the value width are properties of the node, so both are resolved once
    /// here rather than per patch: otherwise every patch pays `ReadUnsigned`'s switch and a
    /// variable-width `Slice(..).CopyTo(..)`, which dominates a scan whose patch set is large.
    /// </remarks>
    public readonly void ApplyAll(ReadOnlySpan<byte> values, int width, Span<byte> destination) =>
        Scatter(values, width, 0, _count, 0, _arrayLength, destination);

    /// <summary>
    /// Scatters patches <c>[first, last)</c>, whose positions all lie in
    /// <c>[start, start + rows)</c>, into a destination that begins at row <paramref name="start"/>.
    /// </summary>
    private readonly void Scatter(
        ReadOnlySpan<byte> values, int width, int first, int last, int start, int rows, Span<byte> destination)
    {
        switch (_indicesPType)
        {
            case PType.U8:
                Scatter<byte>(values, width, first, last, start, rows, destination);
                break;
            case PType.U16:
                Scatter<ushort>(values, width, first, last, start, rows, destination);
                break;
            case PType.U32:
                Scatter<uint>(values, width, first, last, start, rows, destination);
                break;
            default:
                Scatter<ulong>(values, width, first, last, start, rows, destination);
                break;
        }
    }

    private readonly void Scatter<TIndex>(
        ReadOnlySpan<byte> values, int width, int first, int last, int start, int rows, Span<byte> destination)
        where TIndex : unmanaged
    {
        ReadOnlySpan<TIndex> indices = MemoryMarshal.Cast<byte, TIndex>(_indices)[first..last];
        ulong offset = (ulong)_offset + (ulong)start;
        switch (width)
        {
            case 1:
                Scatter<TIndex, byte>(indices, values, first, offset, rows, destination);
                break;
            case 2:
                Scatter<TIndex, ushort>(indices, values, first, offset, rows, destination);
                break;
            case 4:
                Scatter<TIndex, uint>(indices, values, first, offset, rows, destination);
                break;
            case 8:
                Scatter<TIndex, ulong>(indices, values, first, offset, rows, destination);
                break;
            default:
                Span<byte> target = destination[..(rows * width)];
                for (int i = 0; i < indices.Length; i++)
                {
                    int position = (int)(Widen(indices[i]) - offset);
                    values.Slice((first + i) * width, width)
                        .CopyTo(target.Slice(position * width, width));
                }

                break;
        }
    }

    private static void Scatter<TIndex, TValue>(
        ReadOnlySpan<TIndex> indices, ReadOnlySpan<byte> values, int first, ulong offset, int rows, Span<byte> destination)
        where TIndex : unmanaged
        where TValue : unmanaged
    {
        ReadOnlySpan<TValue> source = MemoryMarshal.Cast<byte, TValue>(values).Slice(first, indices.Length);
        Span<TValue> target = MemoryMarshal.Cast<byte, TValue>(destination)[..rows];
        for (int i = 0; i < indices.Length; i++)
        {
            // Validated at construction: every index ascends, is at or above the patch offset, and
            // the last one is inside the array; the caller passes only the patches of its rows, so
            // every position here is in [0, rows).
            target[(int)(Widen(indices[i]) - offset)] = source[i];
        }
    }

    /// <summary>
    /// Sets each patch's verdict (<see cref="Compute.Trilean"/>, one a patch in <paramref name="states"/>) at its row in
    /// <paramref name="bits"/>, which already holds <paramref name="fill"/>'s, the fill's: the index type resolved once.
    /// </summary>
    /// <remarks>
    /// Under a fill that selects no row and patches that are all known, the common case of a sparse column whose fill is
    /// null, a patch only sets its truth, and its known bit under a null fill: a word or two a patch with no branch. Any
    /// other mix sets each state as a row's.
    /// </remarks>
    public readonly void Judge(ReadOnlySpan<byte> states, byte fill, ref Compute.PredicateBits bits)
    {
        states = states[.._count];
        if (fill != Compute.Trilean.True && !states.Contains(Compute.Trilean.Unknown))
        {
            bool known = fill == Compute.Trilean.Unknown;
            switch (_indicesPType)
            {
                case PType.U8:
                    Mark<byte>(states, known, bits.Truth, bits.Known);
                    break;
                case PType.U16:
                    Mark<ushort>(states, known, bits.Truth, bits.Known);
                    break;
                case PType.U32:
                    Mark<uint>(states, known, bits.Truth, bits.Known);
                    break;
                default:
                    Mark<ulong>(states, known, bits.Truth, bits.Known);
                    break;
            }

            return;
        }

        switch (_indicesPType)
        {
            case PType.U8:
                Set<byte>(states, ref bits);
                break;
            case PType.U16:
                Set<ushort>(states, ref bits);
                break;
            case PType.U32:
                Set<uint>(states, ref bits);
                break;
            default:
                Set<ulong>(states, ref bits);
                break;
        }
    }

    /// <summary>The patches' truths, and their known bits where <paramref name="known"/>, over a fill that selects none.</summary>
    private readonly void Mark<TIndex>(ReadOnlySpan<byte> states, bool known, Span<ulong> truth, Span<ulong> knownBits)
        where TIndex : unmanaged
    {
        ReadOnlySpan<TIndex> indices = MemoryMarshal.Cast<byte, TIndex>(_indices)[..states.Length];
        ulong offset = (ulong)_offset;
        if (known)
        {
            for (int i = 0; i < indices.Length; i++)
            {
                int position = (int)(Widen(indices[i]) - offset);
                ulong bit = 1UL << (position & 63);
                truth[position >> 6] |= states[i] == Compute.Trilean.True ? bit : 0;
                knownBits[position >> 6] |= bit;
            }

            return;
        }

        for (int i = 0; i < indices.Length; i++)
        {
            int position = (int)(Widen(indices[i]) - offset);
            truth[position >> 6] |= states[i] == Compute.Trilean.True ? 1UL << (position & 63) : 0;
        }
    }

    /// <summary>Each patch's state as its row's, any fill and any state.</summary>
    private readonly void Set<TIndex>(ReadOnlySpan<byte> states, ref Compute.PredicateBits bits)
        where TIndex : unmanaged
    {
        ReadOnlySpan<TIndex> indices = MemoryMarshal.Cast<byte, TIndex>(_indices)[..states.Length];
        ulong offset = (ulong)_offset;
        for (int i = 0; i < indices.Length; i++)
        {
            bits.SetState((int)(Widen(indices[i]) - offset), states[i]);
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
    /// <param name="patches">The validated patch set, over the full row space.</param>
    /// <param name="values">The patch values, one per patch, at <paramref name="width"/> bytes.</param>
    /// <param name="width">Bytes per value.</param>
    /// <param name="wanted">The selected rows, strictly ascending.</param>
    /// <param name="destination">The selected rows' values, in selection order.</param>
    /// <remarks>
    /// <para>
    /// A merge, not a search per patch: both sides are ascending, so one walk over each is enough.
    /// The patch set is left whole rather than selected into, because pushing a second selection
    /// into the patch children would mean re-basing indices that are expressed in the parent's row
    /// space.
    /// </para>
    /// <para>
    /// A merge walks the longer list, which is why the shorter one drives here. Patches are few as
    /// a ratio and not as a count, and a take is served one batch at a time, so a plain merge
    /// walks the whole patch set on every batch to place at most a row or two. When there are
    /// fewer wanted rows than patches, each wanted row binary-searches the patches from where the
    /// last one landed; when there are more, the merge is already the right shape and is kept. The
    /// test is the two lengths against each other - no threshold, no tuning.
    /// </para>
    /// <para>
    /// The search relies on the ascending order <see cref="Create"/> establishes. That walk may be
    /// skipped on a later batch of a node it has already passed for - skipped because the answer is
    /// known, never because it was not asked - so the ordering this search needs holds either way.
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
                    // `Find` returns the insertion point negated, which is where the next wanted
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
    /// Overwrites the patched rows of <c>[start, start + count)</c>, at their places in a
    /// destination that holds that range alone.
    /// </summary>
    /// <param name="patches">The validated patch set, over the full row space.</param>
    /// <param name="values">The patch values, one per patch, at <paramref name="width"/> bytes.</param>
    /// <param name="width">Bytes per value.</param>
    /// <param name="start">The range's first row.</param>
    /// <param name="count">The range's row count.</param>
    /// <param name="destination">The range's values, <paramref name="count"/> rows.</param>
    /// <remarks>
    /// The range's patches are bounded by two binary searches and then scattered as
    /// <see cref="ApplyAll"/> scatters the whole set, so a window of a chunk pays for its own
    /// patches and not for the chunk's.
    /// </remarks>
    public static void ApplyRange(
        in Patches patches, ReadOnlySpan<byte> values, int width, int start, int count, Span<byte> destination)
    {
        int first = FirstAtOrAfter(in patches, start, 0);
        int last = FirstAtOrAfter(in patches, start + count, first);
        patches.Scatter(values, width, first, last, start, count, destination);
    }

    /// <summary>The first patch in <c>[from, Count]</c> whose position is at or after <paramref name="row"/>.</summary>
    private static int FirstAtOrAfter(in Patches patches, int row, int from)
    {
        int low = from;
        int high = patches.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (patches.GetPosition(middle) < row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// The patch at <paramref name="row"/>, searched in <c>[from, Count)</c>, or the bitwise
    /// complement of the first patch beyond it when there is none.
    /// </summary>
    /// <param name="patches">The patch set, whose positions ascend.</param>
    /// <param name="row">The row to find.</param>
    /// <param name="from">The first patch that may still match.</param>
    internal static int Find(in Patches patches, int row, int from)
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
    /// The patch set of <paramref name="node"/>, validated, and its values: the indices at child <paramref name="first"/>,
    /// the values next at <paramref name="valuesType"/>, then the per-chunk offsets when declared, each decoded once for
    /// every read of the node when <paramref name="retained"/>, for this read alone otherwise.
    /// </summary>
    /// <remarks>
    /// The validation is the full decode's whatever reads the patches - a malformed patch set must be refused whether
    /// or not a take or a range happens to skip the row it corrupts. The per-chunk offsets are read, validated and then
    /// deliberately unused: a range's first patch is found by a binary search over the indices, which they would only
    /// shorten.
    /// </remarks>
    /// <returns>The patch values, one a patch at the width of <paramref name="valuesType"/>.</returns>
    /// <exception cref="VortexFormatException">A child does not decode to its shape, or a value is null.</exception>
    internal static ReadOnlySpan<byte> Read(
        ArrayDecodeContext context, in ArrayNode node, int first, DType valuesType, scoped in PatchesMetadata metadata,
        int length, bool retained, string encodingId, out Patches patches)
    {
        int patchCount = ArrayDecodeContext.CheckedLength(metadata.Length, encodingId, "patch count");
        DType indicesType = context.Types.Primitive(metadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = retained
            ? context.DecodeWholeChild(in node, first, indicesType, patchCount)
            : context.DecodeChild(in node, first, indicesType, patchCount);
        int valuesIndex = retained
            ? context.DecodeWholeChild(in node, first + 1, valuesType, patchCount)
            : context.DecodeChild(in node, first + 1, valuesType, patchCount);

        if (metadata.HasChunkOffsets)
        {
            int chunkOffsetsLength = ArrayDecodeContext.CheckedLength(metadata.ChunkOffsetsLength, encodingId, "patch chunk_offsets_len");
            DType chunkOffsetsType = context.Types.Primitive(metadata.ChunkOffsetsPType, Nullability.NonNullable);
            int chunkOffsets = retained
                ? context.DecodeWholeChild(in node, first + 2, chunkOffsetsType, chunkOffsetsLength)
                : context.DecodeChild(in node, first + 2, chunkOffsetsType, chunkOffsetsLength);
            CompressedValues.RequireIndexChild(
                context, chunkOffsets, metadata.ChunkOffsetsPType, chunkOffsetsLength, encodingId, "patch_chunk_offsets");
        }

        bool walked = context.IsNodeChecked(in node);
        patches = Create(context, in metadata, length, indicesIndex, valuesIndex, encodingId, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // A span is read, so a constant child is expanded into one rather than refused.
        CanonicalNode values = context.Canonical.GetNode(CanonicalSupport.ExpandIfConstant(context, valuesIndex));
        if (values.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(encodingId, "patch_values", values.Kind, "a Primitive");
        }

        if (values.PType != valuesType.PType)
        {
            CompressedThrow.Format(
                $"{encodingId}'s patch values decoded as {values.PType.Name()}; " +
                $"{valuesType.PType.Name()} was required.");
        }

        if (!values.Validity.IsAllValid)
        {
            CompressedThrow.Format($"{encodingId} patch values must not contain nulls.");
        }

        return values.Values.Span;
    }

    /// <summary>
    /// Validates a decoded patch set. <paramref name="indicesNodeIndex"/> and
    /// <paramref name="valuesNodeIndex"/> are the already-decoded children.
    /// </summary>
    /// <param name="ctx">The decode context.</param>
    /// <param name="metadata">The descriptor read out of the encoding's own metadata.</param>
    /// <param name="arrayLength">The parent array's row count.</param>
    /// <param name="indicesNodeIndex">The decoded <c>patch_indices</c> child.</param>
    /// <param name="valuesNodeIndex">
    /// The decoded <c>patch_values</c> child; -1 for values judged in their encoding, which only <see cref="Judge"/> may then
    /// be asked of.
    /// </param>
    /// <param name="encodingId">The encoding asking, for the error messages.</param>
    /// <param name="indicesAlreadyChecked">
    /// <see langword="true"/> when this scan has already walked these indices and found them
    /// ascending, so the walk over them may be skipped. Everything else here is constant time and
    /// always runs.
    /// </param>
    /// <returns>The validated patch set.</returns>
    /// <remarks>
    /// The monotonicity walk is the one step that costs a pass over the indices, and on a take it
    /// would be re-run for every batch of the same node. Ascending is a property of the node's
    /// bytes, not of the batch, so the caller may answer it once per scan through
    /// <see cref="ArrayDecodeContext.IsNodeChecked"/>; where that method answers false the walk
    /// runs in full.
    /// </remarks>
    public static Patches Create(
        ArrayDecodeContext ctx,
        scoped in PatchesMetadata metadata,
        int arrayLength,
        int indicesNodeIndex,
        int valuesNodeIndex,
        string encodingId,
        bool indicesAlreadyChecked = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        int count = ArrayDecodeContext.CheckedLength(metadata.Length, encodingId, "patch count");
        int offset = ArrayDecodeContext.CheckedLength(metadata.Offset, encodingId, "patch offset");

        // An empty patch set is malformed rather than a no-op, so a file carrying one is refused
        // instead of being decoded as an unpatched array.
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

        // Values judged in their encoding rather than decoded (-1) leave the patches their indices alone: the encoding that
        // answered was asked of as many rows.
        if (valuesNodeIndex >= 0 && ctx.Canonical.GetNode(valuesNodeIndex).Length is int values && values != count)
        {
            CompressedThrow.ChildLength(encodingId, "patch_values", values, count);
        }

        // Sorted first, then the two ends. Once the sequence is known to ascend, the endpoints
        // imply the rest: if indices[0] is at or above the offset then every index is, and if the
        // last index is inside the array then every index is. So the only per-index work left is a
        // monotonicity check, which is a shifted compare and belongs to the vector unit.
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
    /// The patch indices must ascend: every lookup path assumes it and the endpoint checks above
    /// rely on it, even though the reference implementation only asserts it in debug builds.
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
