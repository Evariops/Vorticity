using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManagedCompressions.Zstd;
using Vorticity.Arrays;
using Vorticity.File;
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
            _keys[i] = new KeyBlocks(keys[i].Index, keys[i].Key, keys[i].Class, blockRows, stringBoundBytes);
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

    /// <summary>One key and class over a chunk: its blocks that hold a value, each its count and bounds.</summary>
    internal sealed class KeyBlocks(int index, string key, PathClass @class, int blockRows, int stringBoundBytes)
    {
        private readonly List<(int Block, PathCensus.PathCount Count)> _blocks = [];

        /// <summary>The key's index among the shredding's keys.</summary>
        internal int Index => index;

        internal string Key => key;

        internal PathClass Class => @class;

        /// <summary>The blocks of the chunk that hold a value, in order, each counted from the chunk's first.</summary>
        internal IReadOnlyList<(int Block, PathCensus.PathCount Count)> Blocks => _blocks;

        /// <summary>Counts the value <paramref name="scalar"/> of row <paramref name="row"/> of the chunk.</summary>
        internal void Note(int row, VariantScalar scalar)
        {
            int block = row / blockRows;
            if (_blocks.Count == 0 || _blocks[^1].Block != block)
            {
                _blocks.Add((block, new PathCensus.PathCount()));
            }

            _blocks[^1].Count.Note(scalar, stringBoundBytes);
        }
    }
}

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
internal sealed class VariantBlockStats
{
    private const byte Version = 1;

    internal VariantBlockStats(IReadOnlyList<(string Key, PathClass Class)> keys, IReadOnlyList<(int Block, PathEntry Entry)[]> blocks)
    {
        Keys = keys;
        Blocks = blocks;
    }

    /// <summary>The keys and classes counted.</summary>
    internal IReadOnlyList<(string Key, PathClass Class)> Keys { get; }

    /// <summary>Per key and class, its blocks that hold a value in the file's order, each its count and bounds; empty for a region not read.</summary>
    internal IReadOnlyList<(int Block, PathEntry Entry)[]> Blocks { get; }

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

    /// <summary>The region of key <paramref name="key"/>: its blocks, behind a version byte and the body's length, the body one zstd frame.</summary>
    internal byte[] KeyBytes(int key)
    {
        ProtoWriter body = new ProtoWriter();
        byte[] plain;
        try
        {
            (int Block, PathEntry Entry)[] blocks = Blocks[key];
            body.WriteVarint((ulong)blocks.Length);
            int previous = 0;
            foreach ((int block, PathEntry entry) in blocks)
            {
                body.WriteVarint((ulong)(block - previous));
                previous = block;
                body.WriteVarint((ulong)entry.Values);
                body.WriteVarint(Zigzag(entry.Low));
                body.WriteVarint(Zigzag(entry.High));
                body.WriteVarint((entry.MinText is null ? 0 : 1UL) | (entry.MaxText is null ? 0 : 2UL));
                Text(ref body, entry.MinText);
                Text(ref body, entry.MaxText);
            }

            plain = body.WrittenSpan.ToArray();
        }
        finally
        {
            body.Dispose();
        }

        byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(plain.Length)];
        ZstdCompressor encoder = Writing.ZstdEncoders.Rent();
        int written;
        try
        {
            if (encoder.Compress(plain, frame, out _, out written) != System.Buffers.OperationStatus.Done)
            {
                throw new InvalidOperationException("A key's block statistics did not compress into the bound zstd gives them.");
            }
        }
        finally
        {
            Writing.ZstdEncoders.Return(encoder);
        }

        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)plain.Length);
            writer.WriteRawBytes(frame.AsSpan(0, written));
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        static void Text(ref ProtoWriter writer, byte[]? text)
        {
            if (text is not null)
            {
                writer.WriteVarint((ulong)text.Length);
                writer.WriteRawBytes(text);
            }
        }
    }

    /// <summary>The blocks of a key of <paramref name="class"/> its region <paramref name="bytes"/> holds, or null when they do not decode.</summary>
    internal static (int Block, PathEntry Entry)[]? KeyFrom(ReadOnlySpan<byte> bytes, string key, PathClass @class)
    {
        byte[] plain;
        try
        {
            ProtoReader header = new ProtoReader(bytes);
            if (header.ReadVarint() != Version)
            {
                return null;
            }

            ulong length = header.ReadVarint();
            ReadOnlySpan<byte> frame = bytes[(bytes.Length - header.Remaining)..];
            if (length > (ulong)frame.Length * 1_000 || length > int.MaxValue)
            {
                return null;
            }

            plain = new byte[(int)length];
            ZstdDecompressor decoder = ZstdDecoders.Rent();
            try
            {
                if (decoder.Decompress(frame, plain, out _, out int produced) != System.Buffers.OperationStatus.Done || produced != plain.Length)
                {
                    return null;
                }
            }
            finally
            {
                ZstdDecoders.Return(decoder);
            }
        }
        catch (VortexFormatException)
        {
            return null;
        }

        try
        {
            ProtoReader reader = new ProtoReader(plain);
            ulong count = reader.ReadVarint();
            if (count > (ulong)plain.Length)
            {
                return null;
            }

            string[] path = [key];
            (int, PathEntry)[] blocks = new (int, PathEntry)[(int)count];
            int block = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                block = checked(block + (int)reader.ReadVarint());
                long values = checked((long)reader.ReadVarint());
                long low = Unzigzag(reader.ReadVarint());
                long high = Unzigzag(reader.ReadVarint());
                ulong flags = reader.ReadVarint();
                byte[]? min = (flags & 1) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                byte[]? max = (flags & 2) != 0 ? reader.ReadLengthDelimited().ToArray() : null;
                blocks[i] = (block, new PathEntry(path, @class, values, PathPlace.Typed, low, high, false, min, max));
            }

            return reader.End ? blocks : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The block statistics of the variant column at top-level field <paramref name="field"/> of
    /// <paramref name="file"/>, their regions read for the keys and classes <paramref name="wanted"/>
    /// only; null when the file carries none, or they do not decode.
    /// </summary>
    internal static async ValueTask<(VariantBlockStats? Stats, int Segments, long Bytes)> ReadAsync(
        VortexFile file, int field, IReadOnlyCollection<(string Key, PathClass Class)> wanted, CancellationToken cancellationToken)
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
                || entry.Runs is not [IndexRun run] || OptionsFrom(entry.Options) is not { } keys || run.Payload.Count != keys.Length)
            {
                continue;
            }

            (int, PathEntry)[][] blocks = new (int, PathEntry)[keys.Length][];
            using Vorticity.IO.SegmentRequestSet requests = new Vorticity.IO.SegmentRequestSet(run.Payload.Count);
            int[] slots = new int[keys.Length];
            long bytes = 0;
            for (int key = 0; key < keys.Length; key++)
            {
                blocks[key] = [];
                IndexSegment region = run.Payload[key];
                bool asked = false;
                foreach ((string Key, PathClass Class) want in wanted)
                {
                    asked |= want.Class == keys[key].Class && string.Equals(want.Key, keys[key].Key, StringComparison.Ordinal);
                }

                slots[key] = asked ? requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0)) : -1;
                bytes += asked ? region.Length : 0;
            }

            if (requests.Count == 0)
            {
                return (new VariantBlockStats(keys, blocks), 0, 0);
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int key = 0; key < keys.Length; key++)
            {
                if (slots[key] < 0)
                {
                    continue;
                }

                ReadOnlySpan<byte> region = requests.GetBuffer(slots[key]).Span;
                if (!run.Payload[key].Holds(region) || KeyFrom(region, keys[key].Key, keys[key].Class) is not { } read)
                {
                    return (null, requests.Count, bytes);
                }

                blocks[key] = read;
            }

            return (new VariantBlockStats(keys, blocks), requests.Count, bytes);
        }

        return (null, 0, 0);
    }

    private static ulong Zigzag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static long Unzigzag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>
    /// The block statistics of one variant column as its chunks are written: each chunk's bounds added
    /// at the chunk's first block, then kept where they prune.
    /// </summary>
    internal sealed class Builder(IReadOnlyList<(string Key, PathClass Class)> keys)
    {
        private readonly List<(int Block, PathEntry Entry)>[] _blocks = Lists(keys.Count);

        /// <summary>Adds a chunk's bounds, its blocks counted from <paramref name="firstBlock"/>.</summary>
        internal void Add(int firstBlock, ChunkBlockBounds chunk)
        {
            for (int key = 0; key < keys.Count; key++)
            {
                string[] path = [keys[key].Key];
                foreach ((int block, PathCensus.PathCount count) in chunk.Keys[key].Blocks)
                {
                    (long low, long high, bool _, byte[]? min, byte[]? max) = VariantDirectory.Bounds(count, keys[key].Class);
                    _blocks[key].Add((firstBlock + block, new PathEntry(path, keys[key].Class, count.Read(keys[key].Class), PathPlace.Typed, low, high, false, min, max)));
                }
            }
        }

        /// <summary>
        /// The statistics that can prune among the file's <paramref name="blocks"/> blocks, within
        /// <paramref name="maxBytes"/> bytes, those that narrow their blocks least left out first; null
        /// when none can.
        /// </summary>
        internal VariantBlockStats? Build(int blocks, long maxBytes)
        {
            List<(int Key, double Narrowing)> kept = [];
            for (int key = 0; key < keys.Count; key++)
            {
                if (Narrowing(_blocks[key], keys[key].Class, blocks) is double narrowing and > 0)
                {
                    kept.Add((key, narrowing));
                }
            }

            kept.Sort(static (a, b) => b.Narrowing.CompareTo(a.Narrowing));
            List<(string, PathClass)> names = [];
            List<(int, PathEntry)[]> regions = [];
            long bytes = 0;
            foreach ((int key, double _) in kept)
            {
                (int, PathEntry)[] entries = [.. _blocks[key]];
                VariantBlockStats one = new VariantBlockStats([keys[key]], [entries]);
                long size = one.KeyBytes(0).Length;
                if (bytes + size > maxBytes)
                {
                    continue;
                }

                bytes += size;
                names.Add(keys[key]);
                regions.Add(entries);
            }

            return names.Count == 0 ? null : new VariantBlockStats(names, regions);
        }

        /// <summary>
        /// How much the statistics narrow the file's <paramref name="total"/> blocks, from 0 when every
        /// block holds the key over all of its range to 1: a block without the key counts whole, since a
        /// question of the key's class matches nothing there; a block with it, the share of the key's
        /// range its own range leaves out for an integer, and for a text or a boolean whether its bounds
        /// are narrower than the key's.
        /// </summary>
        private static double Narrowing(List<(int Block, PathEntry Entry)> blocks, PathClass @class, int total)
        {
            if (blocks.Count == 0 || total <= 0)
            {
                return 0;
            }

            double narrowed = total - blocks.Count;
            switch (@class)
            {
                case PathClass.Integer:
                {
                    long low = long.MaxValue;
                    long high = long.MinValue;
                    foreach ((int _, PathEntry entry) in blocks)
                    {
                        low = Math.Min(low, entry.Low);
                        high = Math.Max(high, entry.High);
                    }

                    if (high > low)
                    {
                        foreach ((int _, PathEntry entry) in blocks)
                        {
                            narrowed += 1 - (((double)entry.High - entry.Low) / ((double)high - low));
                        }
                    }

                    break;
                }

                case PathClass.String:
                {
                    // Without text bounds, no block's range is known.
                    byte[]? least = null;
                    byte[]? greatest = null;
                    bool unbounded = false;
                    foreach ((int _, PathEntry entry) in blocks)
                    {
                        if (entry.MinText is not { } min)
                        {
                            return narrowed / total;
                        }

                        least = least is null || min.AsSpan().SequenceCompareTo(least) < 0 ? min : least;
                        if (entry.MaxText is not { } max)
                        {
                            unbounded = true;
                        }
                        else if (greatest is null || max.AsSpan().SequenceCompareTo(greatest) > 0)
                        {
                            greatest = max;
                        }
                    }

                    foreach ((int _, PathEntry entry) in blocks)
                    {
                        bool above = entry.MinText!.AsSpan().SequenceCompareTo(least) > 0;
                        bool below = entry.MaxText is { } max && (unbounded || max.AsSpan().SequenceCompareTo(greatest) < 0);
                        narrowed += above || below ? 1 : 0;
                    }

                    break;
                }

                case PathClass.Boolean:
                    foreach ((int _, PathEntry entry) in blocks)
                    {
                        narrowed += entry.Low is 1 or 2 ? 1 : 0;
                    }

                    break;

                default:
                    return 0;
            }

            return narrowed / total;
        }

        private static List<(int, PathEntry)>[] Lists(int count)
        {
            List<(int, PathEntry)>[] lists = new List<(int, PathEntry)>[count];
            for (int i = 0; i < count; i++)
            {
                lists[i] = [];
            }

            return lists;
        }
    }
}
