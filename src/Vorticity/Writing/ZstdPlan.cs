using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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

    private const int ViewSize = 16;
    private const int InlineBytes = 12;
    private const int Block = 16;
    /// <summary>Bytes of a value <see cref="Blend"/> writes without a loop: twelve in the first block, then two more blocks.</summary>
    private const int Reach = 44;

    /// <summary>Bytes past a stream's end that <see cref="LayViews"/> writes over.</summary>
    private const int StreamSlack = 48;

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
        long streamBytes = StreamBytes(arena, node, out int valueCount);
        if (valueCount == 0 || streamBytes > int.MaxValue - StreamSlack)
        {
            return null;
        }

        // Rented, not allocated: pricing zstd needs two buffers the size of the column for a
        // candidate that may lose. A block's frame ends where its values end, so the stream is cut
        // as it is laid out: its end and its values, per block, go into the frame table.
        int blocks = Blocks(rows, frameRows);
        byte[] stream = ArrayPool<byte>.Shared.Rent((int)streamBytes + StreamSlack);
        int[] frames = ArrayPool<int>.Shared.Rent(blocks * FrameFields);
        try
        {
            LayViews(arena, node, stream, frames, blocks, frameRows);
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
    /// The bytes the stream of a <c>VarBinView</c> node's valid values takes, each behind its
    /// <c>u32</c> length, read from the views' sizes alone.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">A varbinview node.</param>
    /// <param name="valueCount">Its valid values.</param>
    private static long StreamBytes(CanonicalArena arena, CanonicalNode node, out int valueCount)
    {
        int rows = node.Length;
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        if (rows == 0 || valid.IsAllInvalid)
        {
            valueCount = 0;
            return 0;
        }

        ref byte views = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewSize)]);
        if (valid.IsAllValid)
        {
            valueCount = rows;
            return Sizes(ref views, rows) + ((long)rows * sizeof(uint));
        }

        ReadOnlySpan<byte> bits = valid.Bits[..((valid.BitOffset + rows + 7) >> 3)];
        return MaskedSizes(ref views, ref MemoryMarshal.GetReference(bits), valid.BitOffset, rows, out valueCount);
    }

    /// <summary>
    /// Lays a <c>VarBinView</c> node's valid values out as the frames store them, each behind its
    /// <c>u32</c> length, and describes where each block of <paramref name="frameRows"/> rows ends.
    /// </summary>
    /// <remarks>
    /// The copy is the encoding: the column's views point at bytes that are neither contiguous nor
    /// length-prefixed, so there is nothing to compress in place. An inline view already is the
    /// wire form, its size then its bytes, and a value in the data buffer is its size then 16-byte
    /// blocks of it: both are written past the value, over what the next one writes, hence
    /// <see cref="StreamSlack"/>.
    /// </remarks>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">A varbinview node.</param>
    /// <param name="stream">
    /// At least <see cref="StreamBytes"/> plus <see cref="StreamSlack"/> bytes; the bytes past the
    /// stream are garbage afterwards.
    /// </param>
    /// <param name="frames">Per block, the end of its values in the stream and the values up to it, cumulative.</param>
    /// <param name="blocks">Blocks of the node.</param>
    /// <param name="frameRows">Rows of a block.</param>
    private static void LayViews(
        CanonicalArena arena, CanonicalNode node, Span<byte> stream, Span<int> frames, int blocks, int frameRows)
    {
        int rows = node.Length;
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        if (valid.IsAllInvalid)
        {
            frames[..(blocks * FrameFields)].Clear();
            return;
        }

        ViewValues strings = new ViewValues(node);
        ReadOnlySpan<byte> views = node.Views.Span[..(rows * ViewSize)];
        ReadOnlySpan<byte> heap = node.DataBufferCount == 1 ? node.GetDataBuffer(0).Span : default;
        bool masked = !valid.IsAllValid;
        ReadOnlySpan<byte> bits = masked ? valid.Bits[..((valid.BitOffset + rows + 7) >> 3)] : default;
        ref byte view = ref MemoryMarshal.GetReference(views);
        ref byte data = ref MemoryMarshal.GetReference(heap);
        ref byte into = ref MemoryMarshal.GetReference(stream);
        ref byte bit = ref MemoryMarshal.GetReference(bits);
        nint at = 0;
        int values = 0;
        for (int block = 0; block < blocks; block++)
        {
            nint row = (nint)Math.Min((long)block * frameRows, rows);
            nint end = (nint)Math.Min((long)(block + 1) * frameRows, rows);
            while (true)
            {
                row = masked
                    ? heap.Length >= Reach
                        ? BlendMasked(ref view, ref data, heap.Length, ref into, ref at, ref bit, valid.BitOffset, row, end, ref values)
                        : LayMasked(ref view, ref data, heap.Length, ref into, ref at, ref bit, valid.BitOffset, row, end, ref values)
                    : heap.Length >= Reach
                        ? Blend(ref view, ref data, heap.Length, ref into, ref at, row, end)
                        : Lay(ref view, ref data, heap.Length, ref into, ref at, row, end);
                if (row == end)
                {
                    break;
                }

                // A value the loop left: in another data buffer, or too near the end of its own for
                // a 16-byte read. The accessor resolves the buffer and refuses a view out of it.
                ReadOnlySpan<byte> value = strings.At((int)row);
                Span<byte> slot = stream[(int)at..];
                BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)value.Length);
                value.CopyTo(slot[sizeof(uint)..]);
                at += sizeof(uint) + value.Length;
                values++;
                row++;
            }

            if (!masked)
            {
                values = (int)end;
            }

            frames[(block * FrameFields) + 1] = (int)at;
            frames[(block * FrameFields) + 2] = values;
        }
    }

    /// <summary>The sizes of <paramref name="rows"/> views, summed into four totals so no add waits on the one before.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Sizes(ref byte views, int rows)
    {
        ulong a = 0;
        ulong b = 0;
        ulong c = 0;
        ulong d = 0;
        nint row = 0;
        for (; row <= rows - 4; row += 4)
        {
            ref byte at = ref Unsafe.Add(ref views, row * ViewSize);
            a += Unsafe.ReadUnaligned<uint>(ref at);
            b += Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref at, ViewSize));
            c += Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref at, 2 * ViewSize));
            d += Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref at, 3 * ViewSize));
        }

        for (; row < rows; row++)
        {
            a += Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref views, row * ViewSize));
        }

        return (long)(a + b + c + d);
    }

    /// <summary><see cref="Sizes"/> over the valid rows alone: a null row's view is garbage, so its size counts for nothing.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MaskedSizes(ref byte views, ref byte bits, int bitOffset, int rows, out int valueCount)
    {
        ulong total = 0;
        nint count = 0;
        for (nint row = 0; row < rows; row++)
        {
            nint at = row + bitOffset;
            nint valid = (Unsafe.Add(ref bits, at >> 3) >> (int)(at & 7)) & 1;
            total += (ulong)(nint)(((nint)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref views, row * ViewSize)) + sizeof(uint)) & -valid);
            count += valid;
        }

        valueCount = (int)count;
        return (long)total;
    }

    /// <summary>
    /// <see cref="Lay"/> without a branch between inline and out-of-line values, which text of
    /// mixed lengths mispredicts about every other row: each row's first block is made both ways
    /// and one kept, and the next <see cref="Reach"/> bytes are copied whatever the value's size.
    /// An inline row reads the first bytes of the data buffer, which must hold that many.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Blend(ref byte views, ref byte heap, nint heapLength, ref byte stream, ref nint at, nint row, nint end)
    {
        Vector128<byte> spread = Vector128.Create((byte)16, 16, 16, 16, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        Vector128<uint> low = Vector128.Create(uint.MaxValue, 0, 0, 0);
        nint to = at;
        for (; row < end; row++)
        {
            Vector128<uint> view = Vector128.LoadUnsafe(ref Unsafe.Add(ref views, row * ViewSize)).AsUInt32();
            nint size = (nint)view.ToScalar();
            nint outside = (InlineBytes - size) >> 63;
            nint offset = (nint)view.GetElement(3) & outside;
            if ((((nint)view.GetElement(2) | ((heapLength - offset - size - Reach) >> 63)) & outside) != 0)
            {
                break;
            }

            ref byte from = ref Unsafe.Add(ref heap, offset);
            ref byte into = ref Unsafe.Add(ref stream, to);
            Vector128<byte> head = Vector128.Shuffle(Vector128.LoadUnsafe(ref from), spread) | (view & low).AsByte();
            Vector128.ConditionalSelect(Vector128.Create((byte)outside), head, view.AsByte()).StoreUnsafe(ref into);
            Vector128.LoadUnsafe(ref from, 12).StoreUnsafe(ref into, 16);
            Vector128.LoadUnsafe(ref from, 28).StoreUnsafe(ref into, 32);
            for (nint copied = Reach; copied < size; copied += Block)
            {
                Vector128.LoadUnsafe(ref from, (nuint)copied).StoreUnsafe(ref into, (nuint)(copied + sizeof(uint)));
            }

            to += sizeof(uint) + size;
        }

        at = to;
        return row;
    }

    /// <summary><see cref="Blend"/> over a column with nulls: a null row stands for an empty inline value the stream does not advance past.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint BlendMasked(
        ref byte views, ref byte heap, nint heapLength, ref byte stream, ref nint at, ref byte bits, int bitOffset,
        nint row, nint end, ref int values)
    {
        Vector128<byte> spread = Vector128.Create((byte)16, 16, 16, 16, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        Vector128<uint> low = Vector128.Create(uint.MaxValue, 0, 0, 0);
        nint to = at;
        nint count = values;
        for (; row < end; row++)
        {
            nint position = row + bitOffset;
            nint valid = -((Unsafe.Add(ref bits, position >> 3) >> (int)(position & 7)) & 1);
            Vector128<uint> view = Vector128.LoadUnsafe(ref Unsafe.Add(ref views, row * ViewSize)).AsUInt32();
            nint size = (nint)view.ToScalar() & valid;
            nint outside = (InlineBytes - size) >> 63;
            nint offset = (nint)view.GetElement(3) & outside;
            if ((((nint)view.GetElement(2) | ((heapLength - offset - size - Reach) >> 63)) & outside) != 0)
            {
                break;
            }

            ref byte from = ref Unsafe.Add(ref heap, offset);
            ref byte into = ref Unsafe.Add(ref stream, to);
            Vector128<byte> head = Vector128.Shuffle(Vector128.LoadUnsafe(ref from), spread) | (view & low).AsByte();
            Vector128.ConditionalSelect(Vector128.Create((byte)outside), head, view.AsByte()).StoreUnsafe(ref into);
            Vector128.LoadUnsafe(ref from, 12).StoreUnsafe(ref into, 16);
            Vector128.LoadUnsafe(ref from, 28).StoreUnsafe(ref into, 32);
            for (nint copied = Reach; copied < size; copied += Block)
            {
                Vector128.LoadUnsafe(ref from, (nuint)copied).StoreUnsafe(ref into, (nuint)(copied + sizeof(uint)));
            }

            to += (sizeof(uint) + size) & valid;
            count -= valid;
        }

        at = to;
        values = (int)count;
        return row;
    }

    /// <summary>
    /// Lays rows <paramref name="row"/> up to <paramref name="end"/> of a column without nulls,
    /// returning <paramref name="end"/>, or the first row whose value it leaves to the caller: one
    /// in another data buffer than the first, or too near the end of it for a 16-byte read.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Lay(ref byte views, ref byte heap, nint heapLength, ref byte stream, ref nint at, nint row, nint end)
    {
        nint to = at;
        for (; row < end; row++)
        {
            Vector128<uint> view = Vector128.LoadUnsafe(ref Unsafe.Add(ref views, row * ViewSize)).AsUInt32();
            nint size = (nint)view.ToScalar();
            if (size > InlineBytes)
            {
                nint offset = (nint)view.GetElement(3);
                if (view.GetElement(2) != 0 || offset + size + (Block - 1) > heapLength)
                {
                    break;
                }

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref stream, to), (uint)size);
                CopyBlocks(ref Unsafe.Add(ref heap, offset), ref Unsafe.Add(ref stream, to + sizeof(uint)), size);
                to += sizeof(uint) + size;
                continue;
            }

            view.AsByte().StoreUnsafe(ref Unsafe.Add(ref stream, to));
            to += sizeof(uint) + size;
        }

        at = to;
        return row;
    }

    /// <summary>
    /// <see cref="Lay"/> over a column with nulls: every row's view is stored, and the stream
    /// advances past the valid ones alone, so a null row's garbage is written over by the next.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint LayMasked(
        ref byte views, ref byte heap, nint heapLength, ref byte stream, ref nint at, ref byte bits, int bitOffset,
        nint row, nint end, ref int values)
    {
        nint to = at;
        nint count = values;
        for (; row < end; row++)
        {
            nint position = row + bitOffset;
            nint valid = (Unsafe.Add(ref bits, position >> 3) >> (int)(position & 7)) & 1;
            Vector128<uint> view = Vector128.LoadUnsafe(ref Unsafe.Add(ref views, row * ViewSize)).AsUInt32();
            nint size = (nint)view.ToScalar();
            if ((size > InlineBytes) & (valid != 0))
            {
                nint offset = (nint)view.GetElement(3);
                if (view.GetElement(2) != 0 || offset + size + (Block - 1) > heapLength)
                {
                    break;
                }

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref stream, to), (uint)size);
                CopyBlocks(ref Unsafe.Add(ref heap, offset), ref Unsafe.Add(ref stream, to + sizeof(uint)), size);
                to += sizeof(uint) + size;
                count++;
                continue;
            }

            view.AsByte().StoreUnsafe(ref Unsafe.Add(ref stream, to));
            to += (sizeof(uint) + size) & -valid;
            count += valid;
        }

        at = to;
        values = (int)count;
        return row;
    }

    /// <summary>Copies <paramref name="size"/> bytes 16 at a time, reading and writing up to 15 past them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyBlocks(ref byte from, ref byte to, nint size)
    {
        for (nint copied = 0; copied < size; copied += Block)
        {
            Vector128.LoadUnsafe(ref Unsafe.Add(ref from, copied)).StoreUnsafe(ref Unsafe.Add(ref to, copied));
        }
    }

    /// <summary>
    /// A primitive column: the stored stream is the valid values back to back, with no length
    /// prefixes, since the decoder scatters them using the fixed width alone.
    /// </summary>
    /// <summary>Bytes past a compacted stream a vector store may write over: one 512-bit vector.</summary>
    private const int CompactSlack = 64;

    /// <summary>
    /// The valid values of rows <paramref name="from"/> to <paramref name="to"/>, laid after the
    /// <paramref name="count"/> already in <paramref name="stream"/>; the new count.
    /// </summary>
    /// <remarks>
    /// Where there are 512-bit vectors, each vector of rows is compressed to its valid lanes in a
    /// register -- <c>vpcompress</c>, whose form that writes memory Zen 4 runs slowly -- and stored
    /// whole at the next free value, the count moving on by the lanes kept: a store past the values
    /// is written over by the next or lies in the stream's <see cref="CompactSlack"/>. The rows
    /// left, and every row elsewhere, are written whether valid or not and the count moves by the
    /// row's bit, a store and an add where a copy a row was a call.
    /// </remarks>
    internal static int Compact<T>(
        ReadOnlySpan<byte> source, in ValidityReader valid, int from, int to, Span<byte> stream, int count)
        where T : unmanaged
    {
        if (valid.IsAllInvalid)
        {
            return count;
        }

        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> into = MemoryMarshal.Cast<byte, T>(stream);
        ReadOnlySpan<byte> bits = valid.Bits;
        int offset = valid.BitOffset;
        bool allValid = valid.IsAllValid;
        ref T value = ref MemoryMarshal.GetReference(values);
        ref T target = ref MemoryMarshal.GetReference(into);
        int i = from;
        if (Compute.WordBytes.IsAccelerated
            && (Unsafe.SizeOf<T>() >= 4 ? Avx512F.IsSupported : Avx512Vbmi2.IsSupported)
            && (long)into.Length * Unsafe.SizeOf<T>() >= ((long)count + (to - from)) * Unsafe.SizeOf<T>() + CompactSlack)
        {
            Compute.WordBytes spread = Compute.WordBytes.Create();
            int lanes = Vector512<T>.Count;
            ulong groupBits = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;
            for (; i + 64 <= to; i += 64)
            {
                ulong word = allValid ? ulong.MaxValue : BitWords.Load(bits, offset + i);
                for (int group = 0; group < 64 / lanes; group++)
                {
                    Vector512<T> row = Vector512.LoadUnsafe(ref value, (nuint)(i + (group * lanes)));
                    Vector512<T> kept = spread.Lanes<T>(word, group);
                    Vector512<T> packed = Unsafe.SizeOf<T>() switch
                    {
                        1 => Avx512Vbmi2.Compress(Vector512<byte>.Zero, kept.AsByte(), row.AsByte()).As<byte, T>(),
                        2 => Avx512Vbmi2.Compress(Vector512<ushort>.Zero, kept.AsUInt16(), row.AsUInt16()).As<ushort, T>(),
                        4 => Avx512F.Compress(Vector512<uint>.Zero, kept.AsUInt32(), row.AsUInt32()).As<uint, T>(),
                        _ => Avx512F.Compress(Vector512<ulong>.Zero, kept.AsUInt64(), row.AsUInt64()).As<ulong, T>(),
                    };
                    packed.StoreUnsafe(ref target, (nuint)count);
                    count += BitOperations.PopCount((word >> (group * lanes)) & groupBits);
                }
            }
        }

        for (; i < to; i++)
        {
            int at = offset + i;
            int bit = allValid ? 1 : (bits[at >> 3] >> (at & 7)) & 1;
            Unsafe.Add(ref target, count) = Unsafe.Add(ref value, i);
            count += bit;
        }

        return count;
    }

    /// <summary><see cref="Compact{T}"/> for a width no integer has: a decimal's sixteen or thirty-two bytes, a row at a time.</summary>
    private static int CompactWide(
        ReadOnlySpan<byte> source, in ValidityReader valid, int from, int to, int width, Span<byte> stream, int count)
    {
        for (int i = from; i < to; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            source.Slice(i * width, width).CopyTo(stream.Slice(count * width, width));
            count++;
        }

        return count;
    }

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
        byte[]? stream = allValid ? null : ArrayPool<byte>.Shared.Rent((rows * width) + CompactSlack);
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
                    int from = (int)Math.Min((long)block * frameRows, rows);
                    valueCount = width switch
                    {
                        1 => Compact<byte>(source, in valid, from, end, stream, valueCount),
                        2 => Compact<ushort>(source, in valid, from, end, stream, valueCount),
                        4 => Compact<uint>(source, in valid, from, end, stream, valueCount),
                        8 => Compact<ulong>(source, in valid, from, end, stream, valueCount),
                        _ => CompactWide(source, in valid, from, end, width, stream, valueCount),
                    };
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
