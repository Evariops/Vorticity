using System;
using System.Collections.Generic;

using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>
/// The <c>vortex.pco</c> node's own metadata: pco's header, and its chunks' page sizes. This is
/// only the wrapper the node declares; the compressed values themselves live in the buffers, in
/// pco's own format, which nothing here parses.
/// </summary>
internal readonly struct PcoWrapperMetadata
{
    private const string MessageName = "PcoMetadata";

    private PcoWrapperMetadata(byte[] header, List<int[]> chunks)
    {
        Header = header;
        Chunks = chunks;
    }

    /// <summary>pco's file header bytes, one per node rather than one per file.</summary>
    internal byte[] Header { get; }

    /// <summary>Per chunk, the value count of each of its pages.</summary>
    internal List<int[]> Chunks { get; }

    /// <summary>Pages across every chunk, which is how many page buffers must follow the metas.</summary>
    internal int PageCount
    {
        get
        {
            int total = 0;
            foreach (int[] chunk in Chunks)
            {
                total += chunk.Length;
            }

            return total;
        }
    }

    /// <summary>Values across every page, which must equal the node's row count.</summary>
    internal long ValueCount
    {
        get
        {
            long total = 0;
            foreach (int[] chunk in Chunks)
            {
                foreach (int page in chunk)
                {
                    total += page;
                }
            }

            return total;
        }
    }

    /// <summary>Reads the message body.</summary>
    /// <param name="metadata">The node's metadata bytes.</param>
    /// <returns>The parsed wrapper metadata.</returns>
    internal static PcoWrapperMetadata Read(ReadOnlySpan<byte> metadata)
    {
        byte[] header = [];
        List<int[]> chunks = [];

        ProtoReader reader = new ProtoReader(metadata);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    header = reader.ReadLengthDelimited().ToArray();
                    break;
                case 2:
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "chunks");
                    chunks.Add(ReadChunk(reader.ReadMessage()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new PcoWrapperMetadata(header, chunks);
    }

    private static int[] ReadChunk(ProtoReader chunk)
    {
        List<int> pages = [];
        while (chunk.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "pages");
                pages.Add(ReadPage(chunk.ReadMessage()));
            }
            else
            {
                chunk.SkipField(wire);
            }
        }

        return [.. pages];
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
}
