using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using ManagedCompressions.Zstd;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

internal readonly partial struct ZstdPlan
{
    /// <summary>
    /// The values below which a column's frames are compressed on its own thread rather than ahead: a frame's worth of
    /// work is a few tens of microseconds, about what handing it to another thread costs.
    /// </summary>
    private const int AheadBytes = 256 << 10;

    /// <summary>The frames compressed ahead for the trial that prices <paramref name="node"/>, when they are its values'; null otherwise.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Ahead? AheadOf(CanonicalArena arena, in CanonicalNode node, ArrayBlobWriter.Workspace workspace, int frameRows) =>
        workspace.Ahead is { } ahead && ahead.Serves(arena, in node, frameRows, workspace.Level) ? ahead : null;

    /// <summary>
    /// The plan <see cref="Compress"/> makes of the frames <paramref name="ahead"/> compressed: each block's frame,
    /// the blocks without a value dropped, closed up in order and described as one thread describes them, kept when
    /// together they beat <paramref name="canonicalSize"/> by the margin.
    /// </summary>
    /// <param name="ahead">The frames, every one of them compressed.</param>
    /// <param name="canonicalSize">The bytes to beat.</param>
    /// <param name="numerator">The margin: the frames must be under this many tenths or quarters of it.</param>
    /// <param name="denominator">Of this many.</param>
    /// <param name="views">Whether the values are text, whose stream a plan refuses past what an array holds.</param>
    private static ZstdPlan? Assemble(Ahead ahead, long canonicalSize, int numerator, int denominator, bool views)
    {
        int blocks = ahead.Blocks;
        long streamBytes = 0;
        long produced = 0;
        int values = 0;
        bool failed = false;
        for (int block = 0; block < blocks; block++)
        {
            (int bytes, int count, int written) = ahead.Frame(block);
            streamBytes += bytes;
            values += count;
            produced += written;
            failed |= written < 0;
        }

        ahead.Note(streamBytes, blocks);
        if (values == 0 || failed || (views && streamBytes > int.MaxValue - StreamSlack))
        {
            return null;
        }

        byte[] destination = WorkArrays.Rent<byte>(checked((int)produced));
        int[] frames = WorkArrays.Rent<int>(blocks * FrameFields);
        int at = 0;
        int frameCount = 0;
        for (int block = 0; block < blocks; block++)
        {
            (int bytes, int count, int written) = ahead.Frame(block);
            if (count == 0)
            {
                // A block of nulls stores no value, and a frame of none would cost its header.
                continue;
            }

            ahead.Room(block).AsSpan(0, written).CopyTo(destination.AsSpan(at));
            at += written;
            frames[frameCount * FrameFields] = at;
            frames[(frameCount * FrameFields) + 1] = bytes;
            frames[(frameCount * FrameFields) + 2] = count;
            frameCount++;
        }

        if (at * (long)denominator >= canonicalSize * (long)numerator)
        {
            WorkArrays.Return(destination);
            WorkArrays.Return(frames);
            return null;
        }

        ahead.Taken = true;
        return new ZstdPlan(destination, frames, frameCount);
    }

    /// <summary>
    /// One column's zstd frames for the chunk being written, compressed ahead of its encoding trials as items of the
    /// piece the chunk's columns run in, on any of the writer's threads: the trial that prices zstd on the column's own
    /// values takes them rather than compress the values on its thread, and its plan is the one it would have made,
    /// since a frame is the same bytes whichever thread and compressor write it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trials are synchronous and run on one thread, and which of them prices zstd, on what, is known only as they
    /// run; so the frames are compressed for a column whose trial priced zstd on its own values the chunk before, on
    /// enough of them to fill several frames (<see cref="Note"/>), and for no other: what a trial will most likely price
    /// again. A column whose next trial does not price zstd wasted them, once, and is not compressed ahead after. The
    /// writer asks it only of a column that keeps its chunk waiting, one that took more than its share of the threads.
    /// </para>
    /// <para>
    /// The frames are the column's as the trial reads it, its values' bytes at the same address, its validity's too,
    /// with the same rows a frame and the same level (<see cref="Serves"/>): the chunk's arena is only read while its
    /// columns are compressed, and a lane's records view the chunk's buffers. Each frame is compressed into a room of
    /// its own, sized for its worst case, and given back once the column is written (<see cref="Finish"/>).
    /// </para>
    /// </remarks>
    internal sealed unsafe class Ahead
    {
        /// <summary>The items a column's frames are cut into at most, past the writer's threads.</summary>
        internal const int MaxItems = 1023;

        /// <summary>Numbers kept per frame: the bytes of its stream, its values, and what it compressed to, -1 when it did not.</summary>
        private const int Fields = 3;

        private int[] _slots = [];
        private byte[]?[] _rooms = [];
        private nint[] _heaps = [];

        private CanonicalArena? _arena;
        private int _node;

        // What the frames are of: the values', or views', address, the data buffers' of text, the validity's bitmap.
        private nint _values;
        private int _heapCount;
        private nint _bits;
        private int _bitOffset;
        private bool _masked;
        private int _rows;

        // A primitive's bytes a value; 0 for text.
        private int _width;
        private int _frameRows;
        private int _level;
        private bool _planned;

        // Whether a trial priced zstd on the planned values, on several frames' worth: the column's next chunk is
        // compressed ahead.
        private bool _reached;

        // The ticks the items took, every thread's.
        private long _ticks;

        /// <summary>Whether the frames of the chunk are compressed ahead: the trial reads them once every item has run.</summary>
        internal bool Ready { get; private set; }

        /// <summary>Whether a trial took the frames into a plan it kept.</summary>
        internal bool Taken { get; set; }

        /// <summary>The blocks of the chunk, a frame each.</summary>
        internal int Blocks { get; private set; }

        /// <summary>The items the frames are cut into.</summary>
        internal int Items { get; private set; }

        /// <summary>The ticks the items took together, on whichever threads ran them: read once every item has run.</summary>
        internal long Ticks => _ticks;

        /// <summary>
        /// Plans the frames of node <paramref name="node"/> of <paramref name="chunk"/>, a column's chunk about to be
        /// written: they are compressed ahead when the column's last trial priced zstd on its values, else only
        /// watched, for its trial to note what it prices.
        /// </summary>
        /// <param name="chunk">The chunk's arena, only read until the column is written.</param>
        /// <param name="node">The column's node in it.</param>
        /// <param name="frameRows">The rows of a frame, the writer's block; 0 for one frame a column.</param>
        /// <param name="level">The column's zstd level, as its workspace has it (<see cref="ArrayBlobWriter.Workspace.Level"/>).</param>
        /// <param name="lanes">The threads the frames may be compressed on; 1 to watch alone.</param>
        /// <returns>The items to run, <see cref="Items"/>; 0 when the frames are not compressed ahead.</returns>
        internal int Plan(CanonicalArena chunk, int node, int frameRows, int level, int lanes)
        {
            bool wanted = _reached;
            _reached = false;
            Finish();
            CanonicalNode values = chunk.GetNode(node);
            int width;
            if (values.Kind == CanonicalKind.Primitive)
            {
                width = values.PType.ByteWidth();
                if (width == 0)
                {
                    return 0;
                }
            }
            else if (values.Kind == CanonicalKind.VarBinView)
            {
                width = 0;
            }
            else
            {
                return 0;
            }

            ValidityReader valid = ValidityReader.Of(chunk, values.Validity);
            if (values.Length == 0 || valid.IsAllInvalid)
            {
                return 0;
            }

            _arena = chunk;
            _node = node;
            _rows = values.Length;
            _width = width;
            _frameRows = frameRows > 0 ? frameRows : int.MaxValue;
            _level = level;
            _masked = !valid.IsAllValid;
            _bits = _masked ? Address(valid.Bits) : 0;
            _bitOffset = _masked ? valid.BitOffset : 0;
            _values = Address(width > 0 ? values.Values.Span : values.Views.Span);
            _heapCount = width > 0 ? 0 : values.DataBufferCount;
            if (_heaps.Length < _heapCount)
            {
                _heaps = new nint[_heapCount];
            }

            for (int i = 0; i < _heapCount; i++)
            {
                _heaps[i] = Address(values.GetDataBuffer(i).Span);
            }

            Blocks = Blocks(_rows, _frameRows);
            _planned = true;
            if (!wanted || lanes <= 1 || Blocks <= 1)
            {
                return 0;
            }

            if (_rooms.Length < Blocks)
            {
                _slots = new int[Blocks * Fields];
                _rooms = new byte[]?[Blocks];
            }

            Items = Math.Min(Blocks, Math.Min(lanes, MaxItems));
            Ready = true;
            return Items;
        }

        /// <summary>
        /// Whether these are the frames of <paramref name="node"/>, a trial's, cut at <paramref name="frameRows"/> and
        /// compressed at <paramref name="level"/>: the planned values' bytes, at the same address, with the same nulls.
        /// </summary>
        internal bool Serves(CanonicalArena arena, in CanonicalNode node, int frameRows, int level)
        {
            if (!_planned || node.Length != _rows || frameRows != _frameRows || level != _level)
            {
                return false;
            }

            if (node.Kind == CanonicalKind.Primitive)
            {
                if (_width == 0 || node.PType.ByteWidth() != _width || Address(node.Values.Span) != _values)
                {
                    return false;
                }
            }
            else if (node.Kind != CanonicalKind.VarBinView || _width != 0 || Address(node.Views.Span) != _values
                || node.DataBufferCount != _heapCount)
            {
                return false;
            }
            else
            {
                for (int i = 0; i < _heapCount; i++)
                {
                    if (Address(node.GetDataBuffer(i).Span) != _heaps[i])
                    {
                        return false;
                    }
                }
            }

            ValidityReader valid = ValidityReader.Of(arena, node.Validity);
            return valid.IsAllValid
                ? !_masked
                : !valid.IsAllInvalid && _masked && Address(valid.Bits) == _bits && valid.BitOffset == _bitOffset;
        }

        /// <summary>
        /// Notes a trial pricing zstd on the planned values, a stream of <paramref name="streamBytes"/> bytes in
        /// <paramref name="blocks"/> frames: enough of them, and the column's next chunk is compressed ahead.
        /// </summary>
        internal void Note(long streamBytes, int blocks) => _reached |= streamBytes >= AheadBytes && blocks > 1;

        /// <summary>Compresses the frames of item <paramref name="item"/>, a run of <see cref="Blocks"/> cut in <see cref="Items"/>, on the calling thread.</summary>
        internal void Run(int item)
        {
            Debug.Assert(Ready && (uint)item < (uint)Items, "An item of frames planned ahead.");
            long began = Stopwatch.GetTimestamp();
            CanonicalArena arena = _arena!;
            CanonicalNode node = arena.GetNode(_node);
            ValidityReader valid = ValidityReader.Of(arena, node.Validity);
            int first = (int)((long)item * Blocks / Items);
            int end = (int)((long)(item + 1) * Blocks / Items);
            ZstdCompressor encoder = ZstdEncoders.Rent(_level);
            try
            {
                for (int block = first; block < end; block++)
                {
                    int from = (int)Math.Min((long)block * _frameRows, _rows);
                    int to = (int)Math.Min((long)(block + 1) * _frameRows, _rows);
                    if (_width > 0)
                    {
                        CompressValues(encoder, node, in valid, block, from, to);
                    }
                    else
                    {
                        CompressViews(encoder, node, in valid, block, from, to);
                    }
                }
            }
            finally
            {
                ZstdEncoders.Return(encoder);
                System.Threading.Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - began);
            }
        }

        /// <summary>Frame <paramref name="block"/>: the bytes of its stream, its values, and what it compressed to, -1 when it did not.</summary>
        internal (int Bytes, int Values, int Written) Frame(int block)
        {
            int at = block * Fields;
            return (_slots[at], _slots[at + 1], _slots[at + 2]);
        }

        /// <summary>The room frame <paramref name="block"/> was compressed into; a block without values has none.</summary>
        internal byte[] Room(int block) => _rooms[block]!;

        /// <summary>
        /// Gives the rooms back once the column is written, and forgets the planned values, keeping only whether their
        /// trial priced zstd, for the column's next chunk.
        /// </summary>
        internal void Finish()
        {
            if (Ready)
            {
                for (int block = 0; block < Blocks; block++)
                {
                    if (_rooms[block] is { } room)
                    {
                        _rooms[block] = null;
                        WorkArrays.Return(room);
                    }
                }
            }

            Ready = false;
            Taken = false;
            _planned = false;
            _arena = null;
            Items = 0;
            _ticks = 0;
        }

        /// <summary><see cref="Finish"/>, and what the trials priced forgotten too: for the next writer.</summary>
        internal void Forget()
        {
            Finish();
            _reached = false;
        }

        private static nint Address(ReadOnlySpan<byte> span) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

        /// <summary>A primitive's frame: the block's values as they lie, or its valid ones laid back to back.</summary>
        private void CompressValues(ZstdCompressor encoder, CanonicalNode node, in ValidityReader valid, int block, int from, int to)
        {
            int width = _width;
            ReadOnlySpan<byte> source = node.Values.Span;
            if (!_masked)
            {
                Keep(encoder, block, source.Slice(from * width, (to - from) * width), to - from);
                return;
            }

            byte[] stream = WorkArrays.Rent<byte>(((to - from) * width) + CompactSlack);
            try
            {
                int count = width switch
                {
                    1 => Compact<byte>(source, in valid, from, to, stream, 0),
                    2 => Compact<ushort>(source, in valid, from, to, stream, 0),
                    4 => Compact<uint>(source, in valid, from, to, stream, 0),
                    8 => Compact<ulong>(source, in valid, from, to, stream, 0),
                    _ => CompactWide(source, in valid, from, to, width, stream, 0),
                };
                Keep(encoder, block, stream.AsSpan(0, count * width), count);
            }
            finally
            {
                WorkArrays.Return(stream);
            }
        }

        /// <summary>A text's frame: the block's valid values, each behind its <c>u32</c> length.</summary>
        private void CompressViews(ZstdCompressor encoder, CanonicalNode node, in ValidityReader valid, int block, int from, int to)
        {
            int rows = _rows;
            ReadOnlySpan<byte> views = node.Views.Span[..(rows * ViewSize)];
            ReadOnlySpan<byte> bits = _masked ? valid.Bits[..((valid.BitOffset + rows + 7) >> 3)] : default;
            ref byte first = ref Unsafe.Add(ref MemoryMarshal.GetReference(views), (nint)from * ViewSize);
            int count = to - from;
            long bytes = _masked
                ? MaskedSizes(ref first, ref MemoryMarshal.GetReference(bits), valid.BitOffset + from, to - from, out count)
                : Sizes(ref first, count) + ((long)count * sizeof(uint));
            if (bytes > int.MaxValue - StreamSlack)
            {
                // A stream no array holds, which the plan refuses whole.
                int at = block * Fields;
                _slots[at] = 0;
                _slots[at + 1] = count;
                _slots[at + 2] = -1;
                return;
            }

            if (count == 0)
            {
                Keep(encoder, block, default, 0);
                return;
            }

            byte[] stream = WorkArrays.Rent<byte>((int)bytes + StreamSlack);
            try
            {
                ViewValues strings = new ViewValues(node);
                ReadOnlySpan<byte> heap = node.DataBufferCount == 1 ? node.GetDataBuffer(0).Span : default;
                nint at = 0;
                int values = 0;
                LayRows(in strings, views, heap, bits, valid.BitOffset, _masked, stream, from, to, ref at, ref values);
                Debug.Assert(at == bytes, "The values laid are those their views size.");
                Keep(encoder, block, stream.AsSpan(0, (int)at), count);
            }
            finally
            {
                WorkArrays.Return(stream);
            }
        }

        /// <summary>Compresses frame <paramref name="block"/>, the stream of its <paramref name="values"/> values, into a room of its own.</summary>
        private void Keep(ZstdCompressor encoder, int block, ReadOnlySpan<byte> stream, int values)
        {
            int at = block * Fields;
            _slots[at] = stream.Length;
            _slots[at + 1] = values;
            if (values == 0)
            {
                _slots[at + 2] = 0;
                return;
            }

            int room = ZstdCompressor.GetMaxCompressedLength(stream.Length);
            byte[] into = WorkArrays.Rent<byte>(room);
            _rooms[block] = into;
            _slots[at + 2] = encoder.Compress(stream, into.AsSpan(0, room), out _, out int written) == OperationStatus.Done
                ? written
                : -1;
        }
    }
}
