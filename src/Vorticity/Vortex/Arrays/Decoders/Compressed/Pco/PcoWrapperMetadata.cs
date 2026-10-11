using System;

using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>
/// The <c>vortex.pco</c> node's own metadata: pco's header, and its chunks' page sizes. This is
/// only the wrapper the node declares; the compressed values themselves live in the buffers, in
/// pco's own format, which nothing here parses.
/// </summary>
/// <remarks>
/// Read in place, from the node's metadata bytes: one pass gives the header and the totals the
/// decoder checks the node against, and the chunks' pages are read again, in order, as the decode
/// walks them. A list of page sizes per chunk, rebuilt by every decode of the node, would be an
/// allocation a page for a column of many hundreds of them.
/// </remarks>
internal readonly ref struct PcoWrapperMetadata
{
    private const string MessageName = "PcoMetadata";

    private readonly ReadOnlySpan<byte> _metadata;

    private PcoWrapperMetadata(
        ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> header, int chunkCount, int pageCount, long valueCount)
    {
        _metadata = metadata;
        Header = header;
        ChunkCount = chunkCount;
        PageCount = pageCount;
        ValueCount = valueCount;
    }

    /// <summary>pco's file header bytes, one per node rather than one per file.</summary>
    internal ReadOnlySpan<byte> Header { get; }

    /// <summary>Chunks, which is how many chunk metas lead the buffers.</summary>
    internal int ChunkCount { get; }

    /// <summary>Pages across every chunk, which is how many page buffers must follow the metas.</summary>
    internal int PageCount { get; }

    /// <summary>Values across every page, which must equal the node's row count.</summary>
    internal long ValueCount { get; }

    /// <summary>Reads the message body.</summary>
    /// <param name="metadata">The node's metadata bytes, which the result reads from.</param>
    /// <returns>The parsed wrapper metadata.</returns>
    internal static PcoWrapperMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ReadOnlySpan<byte> header = default;
        int chunks = 0;
        int pages = 0;
        long values = 0;

        ProtoReader reader = new ProtoReader(metadata);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    header = reader.ReadLengthDelimited();
                    break;
                case 2:
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "chunks");
                    ProtoReader chunk = reader.ReadMessage();
                    chunks++;
                    while (NextPage(ref chunk, out int pageValues))
                    {
                        pages++;
                        values += pageValues;
                    }

                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new PcoWrapperMetadata(metadata, header, chunks, pages, values);
    }

    /// <summary>The chunks, in order, each as the value counts of its pages.</summary>
    internal ChunkEnumerator GetChunks() => new ChunkEnumerator(_metadata);

    /// <summary>The next page of <paramref name="chunk"/>, and how many values it holds.</summary>
    private static bool NextPage(ref ProtoReader chunk, out int values)
    {
        while (chunk.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "pages");
                values = ReadPage(chunk.ReadMessage());
                return true;
            }

            chunk.SkipField(wire);
        }

        values = 0;
        return false;
    }

    private static int ReadPage(ProtoReader page)
    {
        int values = 0;
        while (page.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                values = (int)MetadataProto.ReadUInt32(ref page, wire, MessageName, "n_values");
            }
            else
            {
                page.SkipField(wire);
            }
        }

        return values;
    }

    /// <summary>The chunks of the metadata, in order.</summary>
    internal ref struct ChunkEnumerator
    {
        private ProtoReader _reader;

        internal ChunkEnumerator(ReadOnlySpan<byte> metadata)
        {
            _reader = new ProtoReader(metadata);
            Current = default;
        }

        /// <summary>The current chunk's pages.</summary>
        public PageEnumerator Current { get; private set; }

        /// <summary>This enumerator, for <c>foreach</c>.</summary>
        public readonly ChunkEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next chunk; <see cref="Read"/> has checked every one's shape.</summary>
        public bool MoveNext()
        {
            while (_reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field == 2)
                {
                    Current = new PageEnumerator(_reader.ReadMessage());
                    return true;
                }

                _reader.SkipField(wire);
            }

            return false;
        }
    }

    /// <summary>The value counts of one chunk's pages, in order.</summary>
    internal ref struct PageEnumerator
    {
        private ProtoReader _chunk;
        private int _current;

        internal PageEnumerator(ProtoReader chunk)
        {
            _chunk = chunk;
            _current = 0;
        }

        /// <summary>The current page's value count.</summary>
        public readonly int Current => _current;

        /// <summary>This enumerator, for <c>foreach</c>.</summary>
        public readonly PageEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next page.</summary>
        public bool MoveNext() => NextPage(ref _chunk, out _current);
    }
}
