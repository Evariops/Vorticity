using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
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
internal sealed record PathChunk(ulong Offset, uint Length, byte AlignmentExponent, long FirstRow, long Rows, byte[] DType, byte[] Tree)
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
        Encodings = encodings;
        Keys = keys;
        Chunks = chunks;
    }

    /// <summary>The array encodings of the file, in the order the trees name them.</summary>
    internal IReadOnlyList<string> Encodings { get; }

    /// <summary>The shredded keys of the column, in their order in its tree.</summary>
    internal IReadOnlyList<string> Keys { get; }

    /// <summary>Per key, its chunks in row order; empty for a region not read yet.</summary>
    internal IReadOnlyList<PathChunk[]> Chunks { get; }

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
                writer.WriteRawBytes(chunk.DType);
                writer.WriteVarint((ulong)chunk.Tree.Length);
                writer.WriteRawBytes(chunk.Tree);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
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
    internal static PathChunk[]? KeyFrom(ReadOnlySpan<byte> bytes)
    {
        if (ZstdRegion.Rent(bytes, Version, out int length) is not { } body)
        {
            return null;
        }

        try
        {
            return BodyFrom(body.AsSpan(0, length));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>The chunks a key's body holds, or null when they do not decode.</summary>
    private static PathChunk[]? BodyFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
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
                byte[] dtype = reader.ReadLengthDelimited().ToArray();
                byte[] tree = reader.ReadLengthDelimited().ToArray();
                chunks[i] = new PathChunk(offset, length, alignment, first, rows, dtype, tree);
            }

            return reader.End ? chunks : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The map of the variant column at top-level field <paramref name="field"/> of <paramref name="file"/>,
    /// its regions read for the keys <paramref name="wanted"/> only; null when the file carries none,
    /// or one that does not decode.
    /// </summary>
    internal static async ValueTask<VariantPathMap?> ReadAsync(
        VortexFile file, int field, IReadOnlyCollection<string> wanted, CancellationToken cancellationToken)
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
                || entry.Runs is not [IndexRun run] || OptionsFrom(entry.Options) is not { } options
                || run.Payload.Count != options.Keys.Length)
            {
                continue;
            }

            PathChunk[][] chunks = new PathChunk[options.Keys.Length][];
            using Vorticity.IO.SegmentRequestSet requests = new Vorticity.IO.SegmentRequestSet(run.Payload.Count);
            int[] slots = new int[options.Keys.Length];
            for (int key = 0; key < options.Keys.Length; key++)
            {
                chunks[key] = [];
                IndexSegment region = run.Payload[key];
                slots[key] = wanted.Contains(options.Keys[key])
                    ? requests.Add(new Vorticity.Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0))
                    : -1;
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int key = 0; key < options.Keys.Length; key++)
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

            return new VariantPathMap(options.Encodings, options.Keys, chunks);
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

                using FlatBufferBuilder builder = new FlatBufferBuilder();
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
                    offset + (ulong)first, checked((uint)(end - first)), alignment, 0, 0,
                    Types.Serialization.DTypeProtobuf.Serialize(treeDType.GetField(field)), bytes)));
            }
        }

        /// <summary>The map of the chunks noted, the column's chunks holding <paramref name="chunkRows"/> rows each, under the file's <paramref name="encodings"/>.</summary>
        internal VariantPathMap Build(IReadOnlyList<long> chunkRows, IReadOnlyList<string> encodings)
        {
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
            int[] children = new int[node.ChildCount];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = Copy(builder, node.GetChild(i), low);
            }

            ReadOnlySpan<ushort> indices = node.BufferIndices;
            ushort[] buffers = new ushort[indices.Length];
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = (ushort)(indices[i] - low);
            }

            return ArrayWriter.WriteNode(builder, node.Encoding, node.Metadata, children, buffers, 0);
        }
    }
}
