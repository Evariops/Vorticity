using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Zstd-compressed frames over a column's valid values, with their priced size: one frame per
/// block of the writer's rows, so that a read of a few rows decompresses the frames that hold them
/// rather than the whole column. The stream holds only the valid values, nulls excluded, so the
/// decoder scatters them back across the null slots.
/// </summary>
/// <remarks>
/// A value, since a trial is priced for every column of every chunk and most lose: the arrays it
/// holds are rented, and whoever holds the plan last -- the trial that lost, or the blob that
/// writes it -- gives them back, once.
/// </remarks>
internal readonly struct ZstdPlan
{
    /// <summary>Numbers kept per frame: its end in <see cref="Data"/>, the bytes it decompresses to, its values.</summary>
    private const int FrameFields = 3;

    private readonly int[] _frames;

    private ZstdPlan(byte[] data, int[] frames, int frameCount)
    {
        Data = data;
        _frames = frames;
        FrameCount = frameCount;
    }

    /// <summary>
    /// The compressed frames back to back, exactly as they go into the buffers; the rest is slack in
    /// a pooled rental sized for the worst case. Ownership of the array passes to whoever writes the
    /// plan, or back to the pool through <see cref="Release"/>; after either, it must not be read.
    /// </summary>
    internal byte[] Data { get; }

    /// <summary>Bytes of <see cref="Data"/> that are frames: what the column costs.</summary>
    internal int CompressedLength => _frames[((FrameCount - 1) * FrameFields)];

    /// <summary>How many frames the values are cut into; at least one.</summary>
    internal int FrameCount { get; }

    /// <summary>Frame <paramref name="index"/>: its bytes in <see cref="Data"/>, what it decompresses to and its values.</summary>
    internal (int Start, int Length, int Uncompressed, int Values) FrameAt(int index)
    {
        int at = index * FrameFields;
        int start = index == 0 ? 0 : _frames[at - FrameFields];
        return (start, _frames[at] - start, _frames[at + 1], _frames[at + 2]);
    }

    /// <summary>Hands <see cref="Data"/> and the frame table back to the pool, for a plan nobody wrote.</summary>
    internal void Release()
    {
        ArrayPool<byte>.Shared.Return(Data);
        ReleaseFrames();
    }

    /// <summary>Hands the frame table back to the pool, once the frames are laid out and described.</summary>
    internal void ReleaseFrames() => ArrayPool<int>.Shared.Return(_frames);

    /// <summary>
    /// Compresses a <c>VarBinView</c> or <c>Primitive</c> node's valid values, returning
    /// <see langword="null"/> unless the frames beat <paramref name="canonicalSize"/> by a margin
    /// wide enough to justify the decompression pass every read then pays.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <param name="canonicalSize">The bytes the frames have to beat.</param>
    /// <param name="workspace">
    /// The writer's blob workspace, whose encoder the frames are compressed in and whose block
    /// rows they are cut at; null to compress one frame with a context of the call's own.
    /// </param>
    /// <param name="anyGain">
    /// Whether frames any smaller than <paramref name="canonicalSize"/> are kept: size first leaves
    /// the decompression pass out of the price, and with it the margin that pays for it.
    /// </param>
    internal static ZstdPlan? TryBuild(
        CanonicalArena arena, int nodeIndex, long canonicalSize, ArrayBlobWriter.Workspace? workspace = null,
        bool anyGain = false)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int frameRows = workspace?.FrameRows is > 0 and int rows ? rows : int.MaxValue;
        if (node.Kind == CanonicalKind.Primitive)
        {
            return TryBuildPrimitive(arena, node, canonicalSize, workspace, frameRows, anyGain);
        }

        return node.Kind == CanonicalKind.VarBinView
            ? TryBuildViews(arena, node, canonicalSize, workspace, frameRows, anyGain)
            : null;
    }

    /// <summary>
    /// Text and binary: the stored stream is each valid value behind its <c>u32</c> length, the
    /// frames cut where a block of rows ends.
    /// </summary>
    private static ZstdPlan? TryBuildViews(
        CanonicalArena arena, CanonicalNode node, long canonicalSize, ArrayBlobWriter.Workspace? workspace, int frameRows,
        bool anyGain)
    {
        int rows = node.Length;
        long streamBytes = 0;
        int valueCount = 0;

        // The validity kind is a property of the node, so it is resolved once rather than switched
        // on per row.
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        ViewValues strings = new ViewValues(node);
        for (int i = 0; i < rows; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            valueCount++;
            streamBytes += sizeof(uint) + strings.At(i).Length;
            if (streamBytes > int.MaxValue)
            {
                return null;
            }
        }

        if (valueCount == 0)
        {
            return null;
        }

        // Rented, not allocated: pricing zstd needs two buffers the size of the column for a
        // candidate that may lose. A block's frame ends where its values end, so the stream is cut
        // as it is laid out: its end and its values, per block, go into the frame table.
        int blocks = Blocks(rows, frameRows);
        byte[] stream = ArrayPool<byte>.Shared.Rent((int)streamBytes);
        int[] frames = ArrayPool<int>.Shared.Rent(blocks * FrameFields);
        try
        {
            // The copy is the encoding: the wire form is `u32 length` then the bytes, value after
            // value, and the column's views point at bytes that are neither contiguous nor
            // length-prefixed, so there is nothing to compress in place.
            int offset = 0;
            int values = 0;
            for (int block = 0; block < blocks; block++)
            {
                int end = (int)Math.Min((long)(block + 1) * frameRows, rows);
                for (int i = (int)Math.Min((long)block * frameRows, rows); i < end; i++)
                {
                    if (!valid.IsValid(i))
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> value = strings.At(i);
                    BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset, sizeof(uint)), (uint)value.Length);
                    offset += sizeof(uint);
                    value.CopyTo(stream.AsSpan(offset));
                    offset += value.Length;
                    values++;
                }

                frames[(block * FrameFields) + 1] = offset;
                frames[(block * FrameFields) + 2] = values;
            }

            return Compress(
                workspace, stream.AsSpan(0, (int)streamBytes), frames, blocks,
                canonicalSize, anyGain ? 1 : MarginNumerator, anyGain ? 1 : MarginDenominator);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stream);
        }
    }

    /// <summary>
    /// A primitive column: the stored stream is the valid values back to back, with no length
    /// prefixes, since the decoder scatters them using the fixed width alone.
    /// </summary>
    private static ZstdPlan? TryBuildPrimitive(
        CanonicalArena arena, CanonicalNode node, long canonicalSize, ArrayBlobWriter.Workspace? workspace, int frameRows,
        bool anyGain)
    {
        int width = node.PType.ByteWidth();
        if (width == 0)
        {
            return null;
        }

        int rows = node.Length;
        ReadOnlySpan<byte> source = node.Values.Span;

        // With no null row the compacting loop would copy the source to itself, so the source is
        // compressed as it stands. It has to be sliced at `rows * width`: the arena's block can be
        // longer than the rows this node owns, and a longer input is a different frame.
        bool allValid = node.Validity.IsAllValid;
        int blocks = Blocks(rows, frameRows);
        byte[]? stream = allValid ? null : ArrayPool<byte>.Shared.Rent(rows * width);
        int[] frames = ArrayPool<int>.Shared.Rent(Math.Max(blocks, 1) * FrameFields);
        try
        {
            int valueCount = 0;
            ValidityReader valid = ValidityReader.Of(arena, node.Validity);
            for (int block = 0; block < blocks; block++)
            {
                int end = (int)Math.Min((long)(block + 1) * frameRows, rows);
                if (stream is null)
                {
                    valueCount = end;
                }
                else
                {
                    for (int i = (int)Math.Min((long)block * frameRows, rows); i < end; i++)
                    {
                        if (!valid.IsValid(i))
                        {
                            continue;
                        }

                        source.Slice(i * width, width).CopyTo(stream.AsSpan(valueCount * width, width));
                        valueCount++;
                    }
                }

                frames[(block * FrameFields) + 1] = valueCount * width;
                frames[(block * FrameFields) + 2] = valueCount;
            }

            if (valueCount == 0)
            {
                ArrayPool<int>.Shared.Return(frames);
                return null;
            }

            int streamBytes = valueCount * width;
            ReadOnlySpan<byte> input = stream is null
                ? source[..streamBytes]
                : stream.AsSpan(0, streamBytes);

            // A much wider margin than varbin, because it is a read-time decision: a primitive
            // column's alternative is bit-packing, which decodes several times faster than zstd, so
            // only the large size wins are worth taking and the marginal ones are left to it.
            return Compress(
                workspace, input, frames, blocks, canonicalSize,
                anyGain ? 1 : PrimitiveMarginNumerator, anyGain ? 1 : PrimitiveMarginDenominator);
        }
        finally
        {
            if (stream is not null)
            {
                ArrayPool<byte>.Shared.Return(stream);
            }
        }
    }

    /// <summary>How many blocks of <paramref name="frameRows"/> rows <paramref name="rows"/> make.</summary>
    private static int Blocks(int rows, int frameRows) =>
        rows == 0 ? 0 : (int)(((long)rows + frameRows - 1) / frameRows);

    /// <summary>
    /// Compresses each block's slice of <paramref name="stream"/> into its own frame, dropping the
    /// blocks that hold no value, and keeps the frames when together they beat
    /// <paramref name="canonicalSize"/> by the margin.
    /// </summary>
    /// <param name="workspace">The writer's workspace, or null for a context of the call's own.</param>
    /// <param name="stream">The valid values as the frames store them.</param>
    /// <param name="frames">
    /// Per block, as the caller filled it: the end of its values in the stream and the values up
    /// to it, cumulative. Rewritten in place into the frame table: each kept frame's end in the
    /// compressed bytes, its own decompressed bytes and its own values. The plan takes it on
    /// success; it goes back to the pool otherwise.
    /// </param>
    /// <param name="blocks">Blocks the caller described.</param>
    /// <param name="canonicalSize">The bytes to beat.</param>
    /// <param name="numerator">The margin: the frames must be under this many tenths or quarters of it.</param>
    /// <param name="denominator">Of this many.</param>
    private static ZstdPlan? Compress(
        ArrayBlobWriter.Workspace? workspace, ReadOnlySpan<byte> stream, int[] frames, int blocks,
        long canonicalSize, int numerator, int denominator)
    {
        long bound = 0;
        for (int block = 0, from = 0; block < blocks; block++)
        {
            int to = frames[(block * FrameFields) + 1];
            bound += ZstandardEncoder.GetMaxCompressedLength(to - from);
            from = to;
        }

        if (workspace is { Fan.Lanes: > 1 } && blocks > 1 && stream.Length >= AcrossBytes)
        {
            return CompressAcross(workspace, stream, frames, blocks, bound, canonicalSize, numerator, denominator);
        }

        byte[] destination = ArrayPool<byte>.Shared.Rent(checked((int)bound));
        bool kept = false;
        try
        {
            int written = 0;
            int count = 0;
            for (int block = 0, from = 0, before = 0; block < blocks; block++)
            {
                int to = frames[(block * FrameFields) + 1];
                int through = frames[(block * FrameFields) + 2];
                if (through == before)
                {
                    // A block of nulls stores no value, and a frame of none would cost its header.
                    continue;
                }

                if (!TryCompress(workspace, stream[from..to], destination.AsSpan(written), out int produced))
                {
                    return null;
                }

                // In place: the slot rewritten is this block's or an earlier one, read already.
                written += produced;
                frames[count * FrameFields] = written;
                frames[(count * FrameFields) + 1] = to - from;
                frames[(count * FrameFields) + 2] = through - before;
                count++;
                from = to;
                before = through;
            }

            // The frames' own bytes are the whole cost: the metadata is two varints a frame and the
            // validity child is written either way.
            if (written * (long)denominator >= canonicalSize * (long)numerator)
            {
                return null;
            }

            kept = true;
            return new ZstdPlan(destination, frames, count);
        }
        finally
        {
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
                ArrayPool<int>.Shared.Return(frames);
            }
        }
    }

    /// <summary>
    /// <see cref="Compress"/> on the workspace's threads: each frame compressed into a room of its
    /// own, sized for its worst case, then the frames closed up in order and described as one thread
    /// describes them, so the bytes and the table are the ones one thread writes.
    /// </summary>
    private static unsafe ZstdPlan? CompressAcross(
        ArrayBlobWriter.Workspace workspace, ReadOnlySpan<byte> stream, int[] frames, int blocks, long bound,
        long canonicalSize, int numerator, int denominator)
    {
        ZstdFrames across = workspace.Frames;
        Span<int> plan = across.Plan(blocks);
        int count = 0;
        long at = 0;
        for (int block = 0, from = 0, before = 0; block < blocks; block++)
        {
            int to = frames[(block * FrameFields) + 1];
            int through = frames[(block * FrameFields) + 2];
            if (through == before)
            {
                // A block of nulls stores no value, and a frame of none would cost its header.
                continue;
            }

            int room = (int)ZstandardEncoder.GetMaxCompressedLength(to - from);
            int slot = count * ZstdFrames.Fields;
            plan[slot] = from;
            plan[slot + 1] = to;
            plan[slot + 2] = (int)at;
            plan[slot + 3] = room;
            plan[slot + 4] = 0;
            plan[slot + 5] = through - before;
            at += room;
            count++;
            from = to;
            before = through;
        }

        byte[] destination = ArrayPool<byte>.Shared.Rent(checked((int)bound));
        bool kept = false;
        try
        {
            bool compressed;
            fixed (byte* input = stream)
            fixed (byte* output = destination)
            {
                compressed = across.Compress(workspace.Fan!, input, output, count);
            }

            if (!compressed)
            {
                return null;
            }

            // Closed up left to right: a frame's room starts at or after the end of the frames
            // before it, so each move reads what no earlier move has written over.
            int written = 0;
            for (int frame = 0; frame < count; frame++)
            {
                int slot = frame * ZstdFrames.Fields;
                int produced = plan[slot + 4];
                destination.AsSpan(plan[slot + 2], produced).CopyTo(destination.AsSpan(written));
                written += produced;
                frames[frame * FrameFields] = written;
                frames[(frame * FrameFields) + 1] = plan[slot + 1] - plan[slot];
                frames[(frame * FrameFields) + 2] = plan[slot + 5];
            }

            if (written * (long)denominator >= canonicalSize * (long)numerator)
            {
                return null;
            }

            kept = true;
            return new ZstdPlan(destination, frames, count);
        }
        finally
        {
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
                ArrayPool<int>.Shared.Return(frames);
            }
        }
    }

    /// <summary>
    /// The values below which a column's frames are compressed on the calling thread alone: a
    /// frame's worth of work is a few tens of microseconds, the hand-off to the pool about as much.
    /// </summary>
    private const int AcrossBytes = 256 << 10;

    /// <summary>
    /// One frame of <paramref name="input"/> into <paramref name="destination"/>, which holds the
    /// worst case.
    /// </summary>
    /// <remarks>
    /// A zstd compression context is a megabyte of native memory, and the one-shot makes and frees
    /// one per call: a trial per column per chunk, most of which lose. The workspace's encoder keeps
    /// one for the whole file, created by the first trial. The frame is the same either way: the
    /// input is whole and the room is the worst case, so the frame is written by the one call that
    /// ends it, with the content size in its header, exactly as the one-shot writes it.
    /// </remarks>
    private static bool TryCompress(
        ArrayBlobWriter.Workspace? workspace, ReadOnlySpan<byte> input, Span<byte> destination, out int written)
    {
        if (workspace is null)
        {
            return ZstandardEncoder.TryCompress(input, destination, out written) && written > 0;
        }

        ZstandardEncoder encoder = workspace.Zstd;
        encoder.Reset();
        OperationStatus status = encoder.Compress(input, destination, out int consumed, out written, isFinalBlock: true);
        return status == OperationStatus.Done && consumed == input.Length && written > 0;
    }

    /// <summary>On a primitive column, keep zstd only when it saves at least a quarter.</summary>
    private const int PrimitiveMarginNumerator = 3;

    private const int PrimitiveMarginDenominator = 4;

    /// <summary>Keep zstd only when it saves at least a tenth of the plain form.</summary>
    private const int MarginNumerator = 9;

    private const int MarginDenominator = 10;
}
