using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Indexes;

/// <summary>
/// One chunk of a shredded path: where the path's buffers lie in the file, the rows of the chunk, the
/// dtype the path wears in it, and its encoding tree over those buffers alone.
/// </summary>
/// <param name="Offset">The file offset of the path's first buffer.</param>
/// <param name="Length">The bytes from there to the end of its last buffer.</param>
/// <param name="AlignmentExponent">The widest alignment its buffers ask for.</param>
/// <param name="FirstRow">The chunk's first row in the file.</param>
/// <param name="Rows">The chunk's rows.</param>
/// <param name="DType">The path's dtype in the chunk, a message of the format's dtype: its typed column's, or a variant's when it kept values of its own.</param>
/// <param name="Tree">The path's <c>Array</c> FlatBuffer, its buffers counted from <paramref name="Offset"/>.</param>
internal sealed record PathChunk(ulong Offset, uint Length, byte AlignmentExponent, long FirstRow, long Rows, ReadOnlyMemory<byte> DType, ReadOnlyMemory<byte> Tree)
{
    internal long EndRow => FirstRow + Rows;
}

/// <summary>
/// Where each shredded key of a variant column lies, chunk by chunk: what a reader needs to read one
/// path's buffers and nothing else. An entry of the index directory, <see cref="IndexKinds.VariantPaths"/>,
/// on the variant column: its options hold the file's array encodings and the keys, and its one run a
/// payload region per key, in the keys' order.
/// </summary>
/// <remarks>
/// <para>
/// A chunk of a shredded variant is one segment, its encoding tree at its end, and each key's typed
/// column and values lie together in it, since the writer lays a key's buffers out in its tree's
/// depth-first order. A key's region holds, per chunk, the range of its buffers, its dtype and its tree
/// over those buffers alone, so a read of the key fetches that range and decodes it as a column, the
/// tree inlined as a flat layout inlines one. Trees differ from chunk to chunk, in their widths and
/// their buffers' lengths, so each is kept whole.
/// </para>
/// <para>
/// Like every index it is a hint. A region that does not decode, or a tree that refuses its buffers, is
/// not used, and the chunk is read whole. An append drops it, since it describes the file as its
/// writer cut it.
/// </para>
/// </remarks>
internal sealed class VariantPathMap
{
    private const byte Version = 1;

    internal VariantPathMap(IReadOnlyList<string> encodings, IReadOnlyList<string> keys, IReadOnlyList<PathChunk[]> chunks)
    {
        _encodings = encodings;
        _keys = keys;
        Chunks = chunks;
        _wanted = [];
    }

    /// <summary>A map read from a file: its options kept as they lie, the keys wanted found among them by their bytes.</summary>
    private VariantPathMap(byte[] options, (string Key, int Place)[] wanted, IReadOnlyList<PathChunk[]> chunks)
    {
        _options = options;
        _wanted = wanted;
        Chunks = chunks;
    }

    // A map read keeps its options' bytes and makes strings of them only when asked for every key or encoding: a question
    // asks a key or two of each object, and the names of every key and encoding made at each one were a quarter of what a
    // question on a log attribute allocated (2026-11-08).
    private readonly byte[]? _options;
    private IReadOnlyList<string>? _encodings;
    private IReadOnlyList<string>? _keys;
    private readonly (string Key, int Place)[] _wanted;

    /// <summary>The array encodings of the file, in the order the trees name them.</summary>
    internal IReadOnlyList<string> Encodings => _encodings ??= OptionsFrom(_options)!.Value.Encodings;

    /// <summary>The shredded keys of the column, in their order in its tree.</summary>
    internal IReadOnlyList<string> Keys => _keys ??= OptionsFrom(_options)!.Value.Keys;

    /// <summary>Per key, its chunks in row order; empty for a region not read yet.</summary>
    internal IReadOnlyList<PathChunk[]> Chunks { get; }

    /// <summary>The place among <see cref="Keys"/> of <paramref name="key"/>, the last of equal ones; -1 for none.</summary>
    /// <remarks>A key the read wanted, found or not, is answered from the walk of the options, with no string made of the others.</remarks>
    internal int IndexOf(string key)
    {
        for (int w = 0; w < _wanted.Length; w++)
        {
            if (string.Equals(_wanted[w].Key, key, StringComparison.Ordinal))
            {
                return _wanted[w].Place;
            }
        }

        return LastIndexOf(Keys, key);
    }

    private static int LastIndexOf(IReadOnlyList<string> keys, string key)
    {
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The entry's options: the encodings, then the keys, each its length and its bytes, behind a version byte.</summary>
    internal byte[] OptionBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            Strings(ref writer, Encodings);
            Strings(ref writer, Keys);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The region of key <paramref name="key"/>: behind a version byte and the body's length, its body
    /// as one zstd frame, since the trees of one key differ little from chunk to chunk.
    /// </summary>
    internal byte[] KeyBytes(int key) => ZstdRegion.Encode(Version, Body(Chunks[key]));

    /// <summary>A key's chunks, each its rows, its range, its dtype and its tree.</summary>
    private static byte[] Body(PathChunk[] chunks)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint((ulong)chunks.Length);
            foreach (PathChunk chunk in chunks)
            {
                writer.WriteVarint((ulong)chunk.FirstRow);
                writer.WriteVarint((ulong)chunk.Rows);
                writer.WriteVarint(chunk.Offset);
                writer.WriteVarint(chunk.Length);
                writer.WriteVarint(chunk.AlignmentExponent);
                writer.WriteVarint((ulong)chunk.DType.Length);
                writer.WriteRawBytes(chunk.DType.Span);
                writer.WriteVarint((ulong)chunk.Tree.Length);
                writer.WriteRawBytes(chunk.Tree.Span);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The places among the keys the options <paramref name="bytes"/> hold of each of <paramref name="wanted"/>'s keys, the last
    /// of equal ones, -1 for a key they lack, compared by their bytes as they lie; and the count of their keys. Null when the
    /// options do not decode.
    /// </summary>
    private static ((string Key, int Place)[] Wanted, int Keys)? WantedFrom(ReadOnlySpan<byte> bytes, IReadOnlyCollection<string>? wanted)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version || !Skip(ref reader, bytes.Length))
            {
                return null;
            }

            (string Key, int Place)[] found = wanted is null ? [] : new (string, int)[wanted.Count];
            int w = 0;
            foreach (string key in wanted ?? [])
            {
                found[w++] = (key, -1);
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            for (int place = 0; place < (int)count; place++)
            {
                ReadOnlySpan<byte> key = reader.ReadLengthDelimited();
                for (int k = 0; k < found.Length; k++)
                {
                    if (Buffers.ByteKeys.SameText(key, found[k].Key))
                    {
                        found[k].Place = place;
                    }
                }
            }

            return reader.End ? (found, (int)count) : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>Steps over a list of strings as <see cref="Strings(ref ProtoWriter, IReadOnlyList{string})"/> writes it; false when it does not decode.</summary>
    private static bool Skip(ref ProtoReader reader, int bound)
    {
        ulong count = reader.ReadVarint();
        if (count > (ulong)bound)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            reader.ReadLengthDelimited();
        }

        return true;
    }

    /// <summary>The encodings and keys the options <paramref name="bytes"/> hold, or null when they do not decode.</summary>
    internal static (string[] Encodings, string[] Keys)? OptionsFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            string[]? encodings = Strings(ref reader, bytes.Length);
            string[]? keys = encodings is null ? null : Strings(ref reader, bytes.Length);
            return keys is not null && reader.End ? (encodings!, keys) : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>The chunks a key's region <paramref name="bytes"/> holds, or null when they do not decode.</summary>
    internal static PathChunk[]? KeyFrom(ReadOnlySpan<byte> bytes) =>
        ZstdRegion.Decode(bytes, Version) is { } body ? BodyFrom(body) : null;

    /// <summary>
    /// The chunks a key's body holds, each chunk's dtype and tree slices of the body, which they keep: two arrays a chunk
    /// copied out of a body given back to the pool were a twentieth of what the mix of questions allocated (2026-11-08).
    /// Null when they do not decode.
    /// </summary>
    private static PathChunk[]? BodyFrom(byte[] body)
    {
        try
        {
            ReadOnlySpan<byte> bytes = body;
            ProtoReader reader = new ProtoReader(bytes);
            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            PathChunk[] chunks = new PathChunk[(int)count];
            for (int i = 0; i < chunks.Length; i++)
            {
                long first = checked((long)reader.ReadVarint());
                long rows = checked((long)reader.ReadVarint());
                ulong offset = reader.ReadVarint();
                uint length = checked((uint)reader.ReadVarint());
                byte alignment = checked((byte)reader.ReadVarint());
                ReadOnlyMemory<byte> dtype = Slice(body, reader.ReadLengthDelimited(), reader.Remaining);
                ReadOnlyMemory<byte> tree = Slice(body, reader.ReadLengthDelimited(), reader.Remaining);
                chunks[i] = new PathChunk(offset, length, alignment, first, rows, dtype, tree);
            }

            return reader.End ? chunks : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The place of <paramref name="field"/>, which ends <paramref name="remaining"/> bytes before the end of <paramref name="body"/>, as a slice of it.</summary>
    private static ReadOnlyMemory<byte> Slice(byte[] body, ReadOnlySpan<byte> field, int remaining) =>
        body.AsMemory(body.Length - remaining - field.Length, field.Length);

    /// <summary>
    /// The map of the variant column at top-level field <paramref name="field"/> of <paramref name="file"/>,
    /// its regions read for the keys <paramref name="wanted"/> only, or for every key under null; null when
    /// the file carries none, or one that does not decode.
    /// </summary>
    /// <remarks>
    /// Under <paramref name="question"/>, a map the question read for these keys already is the one it read: the scans of an
    /// object's passes and parts each read it again, its regions decompressed and its chunks made.
    /// </remarks>
    internal static async ValueTask<VariantPathMap?> ReadAsync(
        VortexFile file, int field, IReadOnlyCollection<string>? wanted, CancellationToken cancellationToken, QuestionReads? question = null)
    {
        if (question is null)
        {
            return await ReadCoreAsync(file, field, wanted, cancellationToken).ConfigureAwait(false);
        }

        if (question.TryGetPathMap(file, field, wanted, out VariantPathMap? kept))
        {
            return kept;
        }

        VariantPathMap? read = await ReadCoreAsync(file, field, wanted, cancellationToken).ConfigureAwait(false);
        question.KeepPathMap(file, field, wanted, read);
        return read;
    }

    /// <summary>The map as <see cref="ReadAsync"/> says, read from the file.</summary>
    private static async ValueTask<VariantPathMap?> ReadCoreAsync(
        VortexFile file, int field, IReadOnlyCollection<string>? wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.VariantPaths || entry.ColumnPath is not [uint column] || column != (uint)field
                || entry.Runs is not [IndexRun run] || entry.Options is not { } optionBytes || WantedFrom(optionBytes, wanted) is not { } options
                || run.Payload.Count != options.Keys)
            {
                continue;
            }

            // The regions of the keys wanted alone, or of every key, each read once, and none of the options made strings.
            PathChunk[][] chunks = new PathChunk[options.Keys][];
            Array.Fill(chunks, []);
            using Vorticity.IO.SegmentRequestSet requests = new Vorticity.IO.SegmentRequestSet(Math.Max(1, wanted is null ? options.Keys : options.Wanted.Length));
            int[] slots = new int[options.Keys];
            Array.Fill(slots, -1);
            for (int key = 0; wanted is null && key < options.Keys; key++)
            {
                IndexSegment region = run.Payload[key];
                slots[key] = requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            }

            foreach ((_, int place) in options.Wanted)
            {
                if (place >= 0 && slots[place] < 0)
                {
                    IndexSegment region = run.Payload[place];
                    slots[place] = requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
                }
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int key = 0; key < options.Keys; key++)
            {
                if (slots[key] < 0)
                {
                    continue;
                }

                ReadOnlySpan<byte> bytes = requests.GetBuffer(slots[key]).Span;
                if (!run.Payload[key].Holds(bytes) || KeyFrom(bytes) is not { } read)
                {
                    return null;
                }

                chunks[key] = read;
            }

            return new VariantPathMap(optionBytes, options.Wanted, chunks);
        }

        return null;
    }

    private static void Strings(ref ProtoWriter writer, IReadOnlyList<string> strings)
    {
        writer.WriteVarint((ulong)strings.Count);
        foreach (string text in strings)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            writer.WriteVarint((ulong)utf8.Length);
            writer.WriteRawBytes(utf8);
        }
    }

    private static string[]? Strings(ref ProtoReader reader, int bound)
    {
        ulong count = reader.ReadVarint();
        if (count > (ulong)bound)
        {
            return null;
        }

        string[] strings = new string[(int)count];
        for (int i = 0; i < strings.Length; i++)
        {
            strings[i] = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
        }

        return strings;
    }

    /// <summary>
    /// The map of one shredded variant column as its chunks are written: each chunk's blob read once,
    /// after the writer built it, for where each key's subtree and buffers lie.
    /// </summary>
    internal sealed class Builder(IReadOnlyList<string> keys)
    {
        private readonly List<(int Chunk, PathChunk Entry)>[] _chunks = [.. new object[keys.Count].Select(static _ => new List<(int, PathChunk)>())];

        // One builder for every key's tree of every chunk, cleared between them, and each key's dtype as it was serialized
        // last, which its next chunks share: made anew for each key of each chunk, a consolidation's builders and dtypes
        // came to some 25 MB.
        private readonly FlatBufferBuilder _flat = new FlatBufferBuilder();
        private readonly (Types.DType DType, byte[] Bytes)?[] _dtypes = new (Types.DType, byte[])?[keys.Count];

        /// <summary>The keys, in their order in the column's tree.</summary>
        internal IReadOnlyList<string> Keys => keys;

        /// <summary>Per key, the bytes of its range in the chunk added last; 0 for a key the chunk left out.</summary>
        internal long[] LastBytes { get; } = new long[keys.Count];

        /// <summary>
        /// Notes chunk <paramref name="chunk"/> of the column, whose blob is <paramref name="blob"/>,
        /// written at <paramref name="offset"/>: for each key of its tree, the range of its buffers,
        /// its dtype and its tree over them, the dtypes read from the variant's own metadata. A key
        /// whose buffers do not lie together is left out of the chunk, which a reader then reads whole.
        /// </summary>
        internal void Add(ReadOnlySpan<byte> blob, ulong offset, int chunk)
        {
            Array.Clear(LastBytes);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]);
            ReadOnlySpan<byte> flat = blob[(blob.Length - 4 - length)..^4];
            int budget = VortexLimits.MaxFlatBufferTables;
            ArrayView view = ArrayView.Root(flat, ref budget);
            if (Arrays.Metadata.VariantMetadata.Read(view.Root_.Metadata).ShreddedDType is not { } shredded)
            {
                return;
            }

            Types.DType treeDType = Types.Serialization.DTypeProtobuf.Read(shredded, new Types.DTypeArena());
            if (treeDType.Kind != Types.DTypeKind.Struct)
            {
                return;
            }

            ReadOnlySpan<BufferSpec> specs = view.Buffers;
            long[] starts = new long[specs.Length];
            long cursor = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                starts[i] = cursor + specs[i].Padding;
                cursor = starts[i] + specs[i].Length;
            }

            // The column's node is the variant, its second child the tree: a struct, its validity
            // first when it has one, then one child per key.
            ArrayNodeView tree = view.Root_.GetChild(1);
            int lead = tree.ChildCount - treeDType.FieldCount;
            for (int key = 0; key < keys.Count; key++)
            {
                int field = treeDType.IndexOfField(keys[key]);
                if (field < 0 || lead < 0)
                {
                    continue;
                }

                ArrayNodeView node = tree.GetChild(lead + field);
                int low = int.MaxValue;
                int high = -1;
                int count = 0;
                Span(node, ref low, ref high, ref count);
                if (count > 0 && high - low + 1 != count)
                {
                    continue;
                }

                FlatBufferBuilder builder = _flat;
                builder.Clear();
                int root = Copy(builder, node, count == 0 ? 0 : low);
                BufferSpec[] own = count == 0 ? [] : specs[low..(high + 1)].ToArray();
                byte alignment = 0;
                for (int i = 0; i < own.Length; i++)
                {
                    alignment = Math.Max(alignment, own[i].AlignmentExponent);
                }

                // The range starts where the widest alignment its buffers ask for allows, at or before
                // the first buffer, so that each lies as aligned in it as in the segment, whose own
                // start is aligned at least as widely.
                long first = count == 0 ? 0 : starts[low] & ~((1L << alignment) - 1);
                if (own.Length > 0)
                {
                    own[0] = new BufferSpec((ushort)(starts[low] - first), own[0].AlignmentExponent, own[0].Compression, own[0].Length);
                }

                byte[] bytes = builder.Finish(ArrayWriter.Write(builder, root, own)).ToArray();
                long end = count == 0 ? 0 : starts[high] + specs[high].Length;
                LastBytes[key] = end - first;
                _chunks[key].Add((chunk, new PathChunk(
                    offset + (ulong)first, checked((uint)(end - first)), alignment, 0, 0, DTypeBytes(key, treeDType.GetField(field)), bytes)));
            }
        }

        /// <summary>The bytes of <paramref name="dtype"/>, key <paramref name="key"/>'s in a chunk: those of its chunk before when it is the same.</summary>
        private byte[] DTypeBytes(int key, Types.DType dtype)
        {
            if (_dtypes[key] is { } last && last.DType.Equals(dtype))
            {
                return last.Bytes;
            }

            byte[] bytes = Types.Serialization.DTypeProtobuf.Serialize(dtype);
            _dtypes[key] = (dtype, bytes);
            return bytes;
        }

        /// <summary>
        /// The map of the chunks noted, the column's chunks holding <paramref name="chunkRows"/> rows each, under the file's
        /// <paramref name="encodings"/>: the builder's last use, which gives its scratch back.
        /// </summary>
        internal VariantPathMap Build(IReadOnlyList<long> chunkRows, IReadOnlyList<string> encodings)
        {
            _flat.Dispose();
            long[] firsts = new long[chunkRows.Count + 1];
            for (int chunk = 0; chunk < chunkRows.Count; chunk++)
            {
                firsts[chunk + 1] = firsts[chunk] + chunkRows[chunk];
            }

            return new VariantPathMap(
                encodings,
                keys,
                [.. _chunks.Select(chunks => chunks.Select(c => c.Entry with { FirstRow = firsts[c.Chunk], Rows = chunkRows[c.Chunk] }).ToArray())]);
        }

        /// <summary>The lowest and highest buffer index of <paramref name="node"/>'s subtree, and how many buffers it names.</summary>
        private static void Span(ArrayNodeView node, ref int low, ref int high, ref int count)
        {
            foreach (ushort index in node.BufferIndices)
            {
                low = Math.Min(low, index);
                high = Math.Max(high, index);
                count++;
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                Span(node.GetChild(i), ref low, ref high, ref count);
            }
        }

        /// <summary>Writes <paramref name="node"/>'s subtree again, its buffer indices less <paramref name="low"/>, its statistics left out.</summary>
        private static int Copy(FlatBufferBuilder builder, ArrayNodeView node, int low)
        {
            // On the stack but for a wide node: a node's children and buffers made anew came to 11 MB of a consolidation.
            int count = node.ChildCount;
            Span<int> children = count <= 32 ? stackalloc int[count] : new int[count];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = Copy(builder, node.GetChild(i), low);
            }

            ReadOnlySpan<ushort> indices = node.BufferIndices;
            Span<ushort> buffers = indices.Length <= 32 ? stackalloc ushort[indices.Length] : new ushort[indices.Length];
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = (ushort)(indices[i] - low);
            }

            return ArrayWriter.WriteNode(builder, node.Encoding, node.Metadata, children, buffers, 0);
        }
    }
}
