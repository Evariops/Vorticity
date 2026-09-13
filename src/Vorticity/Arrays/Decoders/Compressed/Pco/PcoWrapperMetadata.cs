// vortex.pco - vortex-pco-0.86.1/src/array.rs, wrapping pco-1.0.3.
//
// THE WRAPPER IS TRIVIAL AND THE PAYLOAD IS NOT, which is the whole shape of this encoding. The
// Vortex node carries protobuf metadata naming pco's file header and, per chunk, how many values
// each of its pages holds; the buffers are the per-chunk metadata blocks followed by the page
// bodies; and there are zero or one validity children. Everything hard is inside those buffers, in
// pco's own format: mode, delta encoding, a bin table and an ANS entropy coder per latent variable.
//
// SO THIS CLASS STOPS WHERE THE FORMAT BEGINS. It parses the wrapper, checks the buffer arithmetic
// the wrapper promises, and then refuses - it does not decode. The refusal is deliberate and is not
// a placeholder that might quietly start returning numbers: a partly-built entropy coder produces
// PLAUSIBLE values, which is the failure this repository has paid for four times over.
// bench/PLAN.md tracks the remaining pieces.
using System;
using System.Collections.Generic;

using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>The <c>vortex.pco</c> node's own metadata: pco's header, and its chunks' page sizes.</summary>
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
