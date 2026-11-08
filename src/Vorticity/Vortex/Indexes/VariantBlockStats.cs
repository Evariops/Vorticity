using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types.Variants;

namespace Vorticity.Indexes;

/// <summary>
/// The values one chunk's shredded keys hold, block by block, counted as the shredder routes them:
/// for each key whose typed column holds every value of its class, its count and bounds per block of
/// the chunk.
/// </summary>
internal sealed class ChunkBlockBounds
{
    private readonly KeyBlocks[] _keys;

    internal ChunkBlockBounds(IReadOnlyList<(int Index, string Key, PathClass Class)> keys, int blockRows, int stringBoundBytes)
    {
        _keys = new KeyBlocks[keys.Count];
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = new KeyBlocks(keys[i].Index, keys[i].Class, blockRows, stringBoundBytes);
        }
    }

    /// <summary>The keys counted, in the order they were given.</summary>
    internal IReadOnlyList<KeyBlocks> Keys => _keys;

    /// <summary>
    /// The keys of <paramref name="plan"/> whose typed column holds every value of its class, each its
    /// index among the plan's keys: the top-level keys shredded as an integer, a text or a boolean.
    /// </summary>
    internal static (int Index, string Key, PathClass Class)[] Counted(VariantShredding plan)
    {
        if (plan.Kind != VariantShreddingKind.Object)
        {
            return [];
        }

        List<(int, string, PathClass)> keys = [];
        for (int i = 0; i < plan.Keys.Count; i++)
        {
            VariantShredding field = plan.Keys[i].Field;
            PathClass? @class = field.Kind != VariantShreddingKind.Primitive ? null : field.Class switch
            {
                VariantClass.Integer => PathClass.Integer,
                VariantClass.String => PathClass.String,
                VariantClass.Boolean => PathClass.Boolean,
                _ => null,
            };
            if (@class is { } counted)
            {
                keys.Add((i, plan.Keys[i].Name, counted));
            }
        }

        return [.. keys];
    }

    /// <summary>Empties the counts for the chunk that follows, their buffers kept: a writer counts a column's chunks in one.</summary>
    internal ChunkBlockBounds Cleared()
    {
        foreach (KeyBlocks key in _keys)
        {
            key.Clear();
        }

        return this;
    }

    /// <summary>
    /// One key and class over a chunk: its blocks that hold a value, each its count and bounds, a block counted at a time
    /// as the rows come in order, and encoded as it closes the way the file's statistics hold it, each block's distance
    /// from the one before counted from the chunk's first.
    /// </summary>
    internal sealed class KeyBlocks(int index, PathClass @class, int blockRows, int stringBoundBytes)
    {
        // The blocks closed, encoded: their count, the first and the last, and the bytes of the first one's distance,
        // which the file's statistics count from the block before the chunk instead.
        private byte[] _bytes = [];
        private int _length;
        private int _count;
        private int _first;
        private int _last;
        private int _firstBytes;

        // The block being counted, -1 before its first value: its values and their bounds, an integer's least and
        // greatest, a boolean's values seen, a text's least and greatest cut to the bound, in buffers of that length.
        private int _block = -1;
        private long _values;
        private long _low = long.MaxValue;
        private long _high = long.MinValue;
        private int _booleans;
        private byte[]? _minText;
        private byte[]? _maxText;
        private int _minTextLength = -1;
        private int _maxTextLength = -1;
        private bool _maxTextCut;

        /// <summary>The key's index among the shredding's keys.</summary>
        internal int Index => index;

        /// <summary>The blocks of the chunk that hold a value, once its rows are all noted.</summary>
        internal int Count
        {
            get
            {
                Close();
                return _count;
            }
        }

        /// <summary>The first block that holds a value, counted from the chunk's first.</summary>
        internal int First => _first;

        /// <summary>The last block that holds a value, counted from the chunk's first.</summary>
        internal int Last => _last;

        /// <summary>The blocks encoded, the first one's distance counted from the chunk's first block.</summary>
        internal ReadOnlySpan<byte> Body
        {
            get
            {
                Close();
                return _bytes.AsSpan(0, _length);
            }
        }

        /// <summary>The bytes of the first block's distance at the head of <see cref="Body"/>.</summary>
        internal int FirstBytes => _firstBytes;

        /// <summary>Empties the counts for the chunk that follows.</summary>
        internal void Clear()
        {
            _length = 0;
            _count = 0;
            _first = 0;
            _last = 0;
            _firstBytes = 0;
            _block = -1;
            _values = 0;
            _low = long.MaxValue;
            _high = long.MinValue;
            _booleans = 0;
            _minTextLength = _maxTextLength = -1;
            _maxTextCut = false;
        }

        /// <summary>Counts the value <paramref name="scalar"/> of row <paramref name="row"/> of the chunk, a value of the key's class.</summary>
        internal void Note(int row, VariantScalar scalar)
        {
            int block = row / blockRows;
            if (block != _block)
            {
                Close();
                _block = block;
            }

            _values++;
            switch (@class)
            {
                case PathClass.Integer:
                    long integer = (long)scalar.Integer;
                    _low = Math.Min(_low, integer);
                    _high = Math.Max(_high, integer);
                    break;
                case PathClass.Boolean:
                    _booleans |= scalar.Integer != 0 ? 2 : 1;
                    break;
                case PathClass.String when stringBoundBytes > 0:
                    Text(scalar.Bytes);
                    break;
                default:
                    break;
            }
        }

        /// <summary>Widens the block's text bounds by <paramref name="text"/>, cut to the bound, as a path's count does.</summary>
        private void Text(ReadOnlySpan<byte> text)
        {
            ReadOnlySpan<byte> cut = text[..Math.Min(text.Length, stringBoundBytes)];
            bool longer = text.Length > stringBoundBytes;
            if (_minTextLength < 0)
            {
                _minText ??= new byte[stringBoundBytes];
                _maxText ??= new byte[stringBoundBytes];
                cut.CopyTo(_minText);
                cut.CopyTo(_maxText);
                _minTextLength = _maxTextLength = cut.Length;
                _maxTextCut = longer;
                return;
            }

            if (cut.SequenceCompareTo(_minText.AsSpan(0, _minTextLength)) < 0)
            {
                cut.CopyTo(_minText);
                _minTextLength = cut.Length;
            }

            int order = cut.SequenceCompareTo(_maxText.AsSpan(0, _maxTextLength));
            if (order > 0)
            {
                cut.CopyTo(_maxText);
                _maxTextLength = cut.Length;
                _maxTextCut = longer;
            }
            else if (order == 0)
            {
                _maxTextCut |= longer;
            }
        }

        /// <summary>
        /// Ends the block being counted, its bounds encoded as a path directory gives them -- an integer's least and
        /// greatest, a boolean's values seen, a text's least and the least text above its greatest -- and starts none.
        /// </summary>
        private void Close()
        {
            if (_block < 0)
            {
                return;
            }

            bool text = @class == PathClass.String && _minTextLength >= 0;
            ReadOnlySpan<byte> max = text ? _maxText.AsSpan(0, _maxTextLength) : default;

            // A greatest text cut to the bound bounds nothing: the least text above it does, its last byte below 0xFF
            // raised and the bytes after it dropped, and there is none when every byte is 0xFF.
            int maxLength = !text ? -1 : !_maxTextCut ? max.Length : max.LastIndexOfAnyExcept(byte.MaxValue) + 1;
            if (maxLength == 0 && _maxTextCut)
            {
                maxLength = -1;
            }

            int most = (5 * 10) + (text ? _minTextLength : 0) + Math.Max(maxLength, 0) + 10;
            if (_length + most > _bytes.Length)
            {
                Array.Resize(ref _bytes, Math.Max(_length + most, Math.Max(256, _bytes.Length * 2)));
            }

            int start = _length;
            Write((ulong)(_block - (_count == 0 ? 0 : _last)));
            if (_count == 0)
            {
                _first = _block;
                _firstBytes = _length - start;
            }

            (long low, long high) = @class switch
            {
                PathClass.Integer => (_low, _high),
                PathClass.Boolean => ((long)_booleans, 0L),
                _ => (0L, 0L),
            };
            Write((ulong)_values);
            Write(Varint.ZigZagEncode64(low));
            Write(Varint.ZigZagEncode64(high));
            Write((text ? 1UL : 0) | (maxLength >= 0 ? 2UL : 0));
            if (text)
            {
                Write((ulong)_minTextLength);
                _minText.AsSpan(0, _minTextLength).CopyTo(_bytes.AsSpan(_length));
                _length += _minTextLength;
            }

            if (maxLength >= 0)
            {
                Write((ulong)maxLength);
                max[..maxLength].CopyTo(_bytes.AsSpan(_length));
                if (_maxTextCut)
                {
                    _bytes[_length + maxLength - 1]++;
                }

                _length += maxLength;
            }

            _last = _block;
            _count++;
            _block = -1;
            _values = 0;
            _low = long.MaxValue;
            _high = long.MinValue;
            _booleans = 0;
            _minTextLength = _maxTextLength = -1;
            _maxTextCut = false;
        }

        private void Write(ulong value) => _length += Varint.Write(_bytes.AsSpan(_length), value);
    }
}

/// <summary>
/// One block's values of a shredded key in a file's block statistics: the block, its values counted, and their bounds as the
/// key's class holds them, a number's least and greatest, a boolean's values seen, or a text's least and greatest cut to the
/// bound, the greatest null when none is above every text.
/// </summary>
internal readonly record struct BlockBound(int Block, long Values, long Low, long High, byte[]? MinText, byte[]? MaxText);

/// <summary>
/// The block statistics of a variant column's shredded keys: per key and class, for each block of the
/// file that holds a value of it, the count and bounds of those values, what a zone map gives a
/// declared column. An entry of the index directory, <see cref="IndexKinds.VariantBlocks"/>, on the
/// variant column, its one run a region per key and class, behind a version byte and the body's
/// length, the body one zstd frame; the keys and classes in its options.
/// </summary>
/// <remarks>
/// <para>
/// A key's typed column holds every value of its class there, an integer of every width, a text, a
/// boolean, so a block with no entry holds none, and a question of that class reads null there and
/// matches nothing. A key shredded as a double is not counted: a float lies beside the typed column,
/// and <c>AsDouble</c> reads both.
/// </para>
/// <para>
/// Statistics are kept where they can prune, which the writer measures on the object's own values: a
/// key whose every block spans the key's whole range, as a scattered duration does, could refute no
/// range a block at a time that the path directory does not refute for the object, and is dropped.
/// Under the index budget, those that narrow their blocks least go first.
/// </para>
/// </remarks>
internal sealed class VariantBlockStats : IDisposable
{
    private const byte Version = 1;

    // The regions of statistics built to be written, a key's each, encoded once.
    private readonly byte[][]? _regions;

    // Whether the blocks read lie in arrays rented for them, which the statistics give back when they are disposed: read
    // at each scan's pruning and dropped after it, where arrays of their own outlived it, 10 % of what the mix of questions
    // kept past a collection (2026-11-08).
    private bool _rented;

    internal VariantBlockStats(IReadOnlyList<(string Key, PathClass Class)> keys, ReadOnlyMemory<BlockBound>[] blocks, bool rented = false)
    {
        Keys = keys;
        Blocks = blocks;
        _rented = rented;
    }

    /// <summary>Statistics built to be written: the keys and classes, and each one's region (<see cref="KeyBytes"/>).</summary>
    private VariantBlockStats(IReadOnlyList<(string Key, PathClass Class)> keys, byte[][] regions)
    {
        ReadOnlyMemory<BlockBound>[] none = new ReadOnlyMemory<BlockBound>[regions.Length];
        Keys = keys;
        Blocks = none;
        _regions = regions;
    }

    /// <summary>The keys and classes counted.</summary>
    internal IReadOnlyList<(string Key, PathClass Class)> Keys { get; }

    /// <summary>
    /// Per key and class, its blocks that hold a value in the file's order, each its count and bounds; empty for a region
    /// not read, and for statistics built to be written, which hold their regions instead.
    /// </summary>
    internal IReadOnlyList<ReadOnlyMemory<BlockBound>> Blocks { get; }

    /// <summary>Gives the arrays of the blocks read back to the pool: no block is read after.</summary>
    public void Dispose()
    {
        if (!_rented)
        {
            return;
        }

        _rented = false;
        foreach (ReadOnlyMemory<BlockBound> blocks in Blocks)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(blocks, out ArraySegment<BlockBound> array) && array.Array is { Length: > 0 } rented)
            {
                System.Buffers.ArrayPool<BlockBound>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>Which of the keys and classes is <paramref name="key"/> of <paramref name="class"/>, or -1.</summary>
    internal int IndexOf(string key, PathClass @class)
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            if (Keys[i].Class == @class && string.Equals(Keys[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The options: the keys, each its length, its bytes and its class, behind a version byte.</summary>
    internal byte[] OptionBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)Keys.Count);
            foreach ((string key, PathClass @class) in Keys)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(key);
                writer.WriteVarint((ulong)utf8.Length);
                writer.WriteRawBytes(utf8);
                writer.WriteVarint((ulong)@class);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The keys and classes the options <paramref name="bytes"/> hold, or null when they do not decode.</summary>
    internal static (string Key, PathClass Class)[]? OptionsFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            (string, PathClass)[] keys = new (string, PathClass)[(int)count];
            for (int i = 0; i < keys.Length; i++)
            {
                string key = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                ulong @class = reader.ReadVarint();
                if (@class > (ulong)PathClass.Other)
                {
                    return null;
                }

                keys[i] = (key, (PathClass)@class);
            }

            return reader.End ? keys : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>The region of key <paramref name="key"/> of statistics built to be written (<see cref="Builder.Build"/>).</summary>
    /// <exception cref="InvalidOperationException">The statistics were read, not built.</exception>
    internal byte[] KeyBytes(int key) =>
        _regions is { } regions ? regions[key] : throw new InvalidOperationException("Block statistics read from a file hold no region to write.");

    /// <summary>
    /// The region of a key whose <paramref name="count"/> blocks <paramref name="blocks"/> holds encoded (<see cref="Builder"/>):
    /// behind a version byte and the body's length, the body, their count then their bytes, one zstd frame.
    /// </summary>
    private static byte[] Encode(int count, ReadOnlySpan<byte> blocks)
    {
        int length = Varint.Size((ulong)count) + blocks.Length;
        byte[] plain = Aggregating.WorkArrays.Rent<byte>(length);
        try
        {
            blocks.CopyTo(plain.AsSpan(Varint.Write(plain, (ulong)count)));
            return ZstdRegion.Encode(Version, plain.AsSpan(0, length));
        }
        finally
        {
            Aggregating.WorkArrays.Return(plain);
        }
    }

    /// <summary>The blocks of a key its region <paramref name="bytes"/> holds, or null when they do not decode.</summary>
    /// <remarks>
    /// A block is a struct of its number, count and bounds in one array, where it was an entry of a path directory of its
    /// own, a record of ten fields, read at every question: 15 % of what a question on a log attribute allocated (2026-11-08).
    /// </remarks>
    internal static ReadOnlyMemory<BlockBound>? KeyFrom(ReadOnlySpan<byte> bytes)
    {
        if (ZstdRegion.Rent(bytes, Version, out int length) is not { } plain)
        {
            return null;
        }

        try
        {
            ProtoReader reader = new ProtoReader(plain.AsSpan(0, length));
            ulong count = reader.ReadVarint();
            if (count > (ulong)length)
            {
                return null;
            }

            // Rented, at least as long: the statistics give it back when they are disposed.
            BlockBound[] blocks = count == 0 ? [] : System.Buffers.ArrayPool<BlockBound>.Shared.Rent((int)count);
            int block = 0;
            for (int i = 0; i < (int)count; i++)
            {
                block = checked(block + (int)reader.ReadVarint());
                long values = checked((long)reader.ReadVarint());
                long low = Varint.ZigZagDecode64(reader.ReadVarint());
                long high = Varint.ZigZagDecode64(reader.ReadVarint());
                ulong flags = reader.ReadVarint();
                byte[]? min = (flags & 1) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                byte[]? max = (flags & 2) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                blocks[i] = new BlockBound(block, values, low, high, min, max);
            }

            if (reader.End)
            {
                return blocks.AsMemory(0, (int)count);
            }

            if (blocks.Length != 0)
            {
                System.Buffers.ArrayPool<BlockBound>.Shared.Return(blocks, clearArray: true);
            }

            return null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(plain);
        }
    }

    /// <summary>
    /// The block statistics of the variant column at top-level field <paramref name="field"/> of
    /// <paramref name="file"/>, their regions read for the keys and classes <paramref name="wanted"/>
    /// only; null when the file carries none, or they do not decode. Their blocks lie in rented arrays, which the caller
    /// gives back by disposing them once its pruning is done: read again at each scan, they die with it, where kept for
    /// the question's other scans they outlived its collections.
    /// </summary>
    internal static async ValueTask<(VariantBlockStats? Stats, int Segments, long Bytes)> ReadAsync(
        VortexFile file, int field, IReadOnlyList<(string Key, PathClass Class)> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, 0, 0);
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.VariantBlocks || entry.ColumnPath is not [uint column] || column != (uint)field
                || entry.Runs is not [IndexRun run] || WantedFrom(entry.Options, wanted, run.Payload.Count) is not { } found)
            {
                continue;
            }

            // The keys wanted the statistics hold, alone: the options name every key the column's statistics keep, and each
            // made a string at every question allocated more than the statistics read.
            (string, PathClass)[] keys = new (string, PathClass)[found.Count];
            ReadOnlyMemory<BlockBound>[] blocks = new ReadOnlyMemory<BlockBound>[found.Count];
            if (found.Count == 0)
            {
                return (new VariantBlockStats(keys, blocks), 0, 0);
            }

            using Vorticity.IO.SegmentRequestSet requests = new Vorticity.IO.SegmentRequestSet(found.Count);
            long bytes = 0;
            for (int k = 0; k < found.Count; k++)
            {
                keys[k] = (found[k].Key, found[k].Class);
                IndexSegment region = run.Payload[found[k].Place];
                requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
                bytes += region.Length;
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int k = 0; k < found.Count; k++)
            {
                ReadOnlySpan<byte> region = requests.GetBuffer(k).Span;
                if (!run.Payload[found[k].Place].Holds(region) || KeyFrom(region) is not { } read)
                {
                    new VariantBlockStats(keys, blocks, rented: true).Dispose();
                    return (null, requests.Count, bytes);
                }

                blocks[k] = read;
            }

            return (new VariantBlockStats(keys, blocks, rented: true), requests.Count, bytes);
        }

        return (null, 0, 0);
    }

    /// <summary>
    /// The keys of <paramref name="wanted"/> the options <paramref name="bytes"/> hold, each its place among them, in their
    /// order there, compared by their bytes as they lie; null when the options do not decode, or name other than
    /// <paramref name="regions"/> keys.
    /// </summary>
    private static List<(string Key, PathClass Class, int Place)>? WantedFrom(
        ReadOnlySpan<byte> bytes, IReadOnlyList<(string Key, PathClass Class)> wanted, int regions)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length || count != (ulong)regions)
            {
                return null;
            }

            List<(string, PathClass, int)> found = [];
            for (int place = 0; place < (int)count; place++)
            {
                ReadOnlySpan<byte> key = reader.ReadLengthDelimited();
                ulong @class = reader.ReadVarint();
                if (@class > (ulong)PathClass.Other)
                {
                    return null;
                }

                // By index: a foreach over the list boxes its enumerator, once for each key of the object.
                for (int w = 0; w < wanted.Count; w++)
                {
                    if (wanted[w].Class == (PathClass)@class && Buffers.ByteKeys.SameText(key, wanted[w].Key))
                    {
                        found.Add((wanted[w].Key, (PathClass)@class, place));
                        break;
                    }
                }
            }

            return reader.End ? found : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The block statistics of one variant column as its chunks are written: each chunk's bounds added at the chunk's
    /// first block, each key's blocks held encoded as its region's body holds them until the file ends, a dozen bytes a
    /// block of an integer key rather than a path directory's entry, then kept where they prune.
    /// </summary>
    internal sealed class Builder(IReadOnlyList<(string Key, PathClass Class)> keys)
    {
        private readonly KeyStream[] _streams = Streams(keys.Count);

        /// <summary>Adds a chunk's bounds, its blocks counted from <paramref name="firstBlock"/>.</summary>
        internal void Add(int firstBlock, ChunkBlockBounds chunk)
        {
            for (int key = 0; key < keys.Count; key++)
            {
                _streams[key].Append(firstBlock, chunk.Keys[key]);
            }
        }

        /// <summary>
        /// The statistics that can prune among the file's <paramref name="blocks"/> blocks, within
        /// <paramref name="maxBytes"/> bytes, those that narrow their blocks least left out first; null
        /// when none can. The builder's last use: its streams' bytes go back to the process's shelf.
        /// </summary>
        internal VariantBlockStats? Build(int blocks, long maxBytes)
        {
            try
            {
                return Built(blocks, maxBytes);
            }
            finally
            {
                // The builder's last use: its streams' bytes back to the shelf, the regions kept copies of them.
                foreach (KeyStream stream in _streams)
                {
                    stream.GiveBack();
                }
            }
        }

        private VariantBlockStats? Built(int blocks, long maxBytes)
        {
            List<(int Key, double Narrowing)> kept = [];
            for (int key = 0; key < keys.Count; key++)
            {
                if (Narrowing(_streams[key], keys[key].Class, blocks) is double narrowing and > 0)
                {
                    kept.Add((key, narrowing));
                }
            }

            kept.Sort(static (a, b) => b.Narrowing.CompareTo(a.Narrowing));
            List<(string, PathClass)> names = [];
            List<byte[]> regions = [];
            long bytes = 0;
            foreach ((int key, double _) in kept)
            {
                byte[] region = Encode(_streams[key].Count, _streams[key].Body);
                if (bytes + region.Length > maxBytes)
                {
                    continue;
                }

                bytes += region.Length;
                names.Add(keys[key]);
                regions.Add(region);
            }

            return names.Count == 0 ? null : new VariantBlockStats(names, [.. regions]);
        }

        /// <summary>
        /// How much the statistics narrow the file's <paramref name="total"/> blocks, from 0 when every
        /// block holds the key over all of its range to 1: a block without the key counts whole, since a
        /// question of the key's class matches nothing there; a block with it, the share of the key's
        /// range its own range leaves out for an integer, and for a text or a boolean whether its bounds
        /// are narrower than the key's.
        /// </summary>
        private static double Narrowing(KeyStream stream, PathClass @class, int total)
        {
            if (stream.Count == 0 || total <= 0)
            {
                return 0;
            }

            double narrowed = total - stream.Count;
            switch (@class)
            {
                case PathClass.Integer:
                {
                    long low = long.MaxValue;
                    long high = long.MinValue;
                    for (BoundsReader block = stream.Read(); block.MoveNext();)
                    {
                        low = Math.Min(low, block.Low);
                        high = Math.Max(high, block.High);
                    }

                    if (high > low)
                    {
                        for (BoundsReader block = stream.Read(); block.MoveNext();)
                        {
                            narrowed += 1 - (((double)block.High - block.Low) / ((double)high - low));
                        }
                    }

                    break;
                }

                case PathClass.String:
                {
                    // Without text bounds, no block's range is known.
                    ReadOnlySpan<byte> least = default;
                    ReadOnlySpan<byte> greatest = default;
                    bool anyLeast = false;
                    bool anyGreatest = false;
                    bool unbounded = false;
                    for (BoundsReader block = stream.Read(); block.MoveNext();)
                    {
                        if (!block.HasMin)
                        {
                            return narrowed / total;
                        }

                        if (!anyLeast || block.Min.SequenceCompareTo(least) < 0)
                        {
                            least = block.Min;
                            anyLeast = true;
                        }

                        if (!block.HasMax)
                        {
                            unbounded = true;
                        }
                        else if (!anyGreatest || block.Max.SequenceCompareTo(greatest) > 0)
                        {
                            greatest = block.Max;
                            anyGreatest = true;
                        }
                    }

                    for (BoundsReader block = stream.Read(); block.MoveNext();)
                    {
                        bool above = block.Min.SequenceCompareTo(least) > 0;
                        bool below = block.HasMax && (unbounded || block.Max.SequenceCompareTo(greatest) < 0);
                        narrowed += above || below ? 1 : 0;
                    }

                    break;
                }

                case PathClass.Boolean:
                    for (BoundsReader block = stream.Read(); block.MoveNext();)
                    {
                        narrowed += block.Low is 1 or 2 ? 1 : 0;
                    }

                    break;

                default:
                    return 0;
            }

            return narrowed / total;
        }

        private static KeyStream[] Streams(int count)
        {
            KeyStream[] streams = new KeyStream[count];
            for (int i = 0; i < count; i++)
            {
                streams[i] = new KeyStream();
            }

            return streams;
        }

        /// <summary>
        /// One key's blocks so far, encoded as its region's body holds them after their count: each block's distance from
        /// the one before, its values, its bounds zigzagged, which texts follow, and those texts.
        /// </summary>
        private sealed class KeyStream
        {
            private byte[] _bytes = [];
            private int _length;
            private int _previous;

            /// <summary>The blocks held.</summary>
            internal int Count { get; private set; }

            internal ReadOnlySpan<byte> Body => _bytes.AsSpan(0, _length);

            /// <summary>
            /// Holds a chunk's blocks of the key, <paramref name="chunk"/>, past the last held, the chunk's first block
            /// being the file's <paramref name="firstBlock"/>: its blocks as it encoded them, the first one's distance
            /// counted again from the last block held.
            /// </summary>
            internal void Append(int firstBlock, ChunkBlockBounds.KeyBlocks chunk)
            {
                int count = chunk.Count;
                if (count == 0)
                {
                    return;
                }

                ReadOnlySpan<byte> rest = chunk.Body[chunk.FirstBytes..];
                if (_length + 10 + rest.Length > _bytes.Length)
                {
                    // The process's shelf's, at a power of two, which the next object's streams find again.
                    int size = Math.Max(_length + 10 + rest.Length, Math.Max(256, _bytes.Length * 2));
                    byte[] grown = Aggregating.ArrayShelf.Retained.Take<byte>((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)size), zeroed: false);
                    Body.CopyTo(grown);
                    GiveBack();
                    _bytes = grown;
                }

                Write((ulong)(firstBlock + chunk.First - _previous));
                rest.CopyTo(_bytes.AsSpan(_length));
                _length += rest.Length;
                _previous = firstBlock + chunk.Last;
                Count += count;
            }

            /// <summary>The blocks held, read back in order.</summary>
            internal BoundsReader Read() => new BoundsReader(Body, Count);

            /// <summary>Gives the stream's bytes back to the process's shelf; the stream holds none after.</summary>
            internal void GiveBack()
            {
                if (_bytes.Length > 0)
                {
                    Aggregating.ArrayShelf.Retained.Give(_bytes);
                    _bytes = [];
                }
            }

            private void Write(ulong value) => _length += Varint.Write(_bytes.AsSpan(_length), value);

        }

        /// <summary>The blocks of a key's stream, one at a time, their texts views of its bytes.</summary>
        private ref struct BoundsReader
        {
            private ProtoReader _reader;
            private int _left;

            internal BoundsReader(ReadOnlySpan<byte> body, int count)
            {
                _reader = new ProtoReader(body);
                _left = count;
            }

            internal int Block { get; private set; }

            internal long Values { get; private set; }

            internal long Low { get; private set; }

            internal long High { get; private set; }

            internal bool HasMin { get; private set; }

            internal bool HasMax { get; private set; }

            internal ReadOnlySpan<byte> Min { get; private set; }

            internal ReadOnlySpan<byte> Max { get; private set; }

            internal bool MoveNext()
            {
                if (_left == 0)
                {
                    return false;
                }

                _left--;
                Block += (int)_reader.ReadVarint();
                Values = (long)_reader.ReadVarint();
                Low = Varint.ZigZagDecode64(_reader.ReadVarint());
                High = Varint.ZigZagDecode64(_reader.ReadVarint());
                ulong texts = _reader.ReadVarint();
                HasMin = (texts & 1) != 0;
                HasMax = (texts & 2) != 0;
                Min = HasMin ? _reader.ReadLengthDelimited() : default;
                Max = HasMax ? _reader.ReadLengthDelimited() : default;
                return true;
            }
        }
    }
}
